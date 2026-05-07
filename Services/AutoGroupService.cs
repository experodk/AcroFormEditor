using System.Text.RegularExpressions;
using Expero.AcroFormEditor.Models;

namespace Expero.AcroFormEditor.Services;

/// <summary>
/// Analyses extracted <see cref="AcroFieldModel"/> lists and automatically
/// groups fields that are copies of each other using multiple heuristics.
/// </summary>
public partial class AutoGroupService
{
    // ── Scoring weights 
    private const float NameWeight       = 0.50f;
    private const float RectWeight       = 0.15f;
    private const float FontWeight       = 0.15f;
    private const float MultiPageWeight  = 0.20f;

    /// <summary>Minimum confidence to auto-apply a group without user review.</summary>
    public const float AutoApplyThreshold = 0.60f;

    // ── Public API

    /// <summary>
    /// Detects groups of related fields and returns them as
    /// <see cref="FieldGroup"/> instances with confidence scores.
    /// Does NOT mutate the input models — call <see cref="ApplyGroups"/>
    /// to actually set the Role / ParentId.
    /// </summary>
    public List<FieldGroup> DetectGroups(List<AcroFieldModel> fields)
    {
        // Step 1: compute base names
        foreach (var f in fields)
            f.BaseName = ComputeBaseName(f.FullName);

        // Step 2: group candidates by base name
        var baseNameBuckets = fields
            .Where(f => f.FieldType != AcroFieldType.Button) // exclude utility buttons
            .GroupBy(f => f.BaseName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        var groups = new List<FieldGroup>();

        foreach (var bucket in baseNameBuckets)
        {
            var members    = bucket.ToList();
            var primary    = ElectPrimary(members);
            var references = members.Where(m => m.Id != primary.Id).ToList();

            var reasons = new List<string>();
            float score = 0f;

            // ── Heuristic 1: Name similarity
            score += ScoreNameSimilarity(primary, references, reasons);

            // ── Heuristic 2: Same rectangle size
            score += ScoreRectangleMatch(primary, references, reasons);

            // ── Heuristic 3: Same font + size
            score += ScoreFontMatch(primary, references, reasons);

            // ── Heuristic 4: Multi-page presence
            score += ScoreMultiPage(references, reasons);

            groups.Add(new FieldGroup
            {
                GroupName         = primary.BaseName,
                PrimaryFieldId    = primary.Id,
                ReferenceFieldIds = references.Select(k => k.Id).ToList(),
                Confidence        = Math.Clamp(score, 0f, 1f),
                MatchReasons      = reasons
            });
        }

        return groups.OrderByDescending(g => g.Confidence).ToList();
    }

    /// <summary>
    /// Applies the given groups to the field list by setting Role and ParentId.
    /// </summary>
    public void ApplyGroups(List<AcroFieldModel> fields, List<FieldGroup> groups)
    {
        // Reset all to standalone first
        foreach (var f in fields)
        {
            if (!f.IsManuallyGrouped)
            {
                f.Role     = FieldRole.Standalone;
                f.ParentId = null;
                f.GroupConfidence = null;
            }
        }

        var fieldMap = fields.ToDictionary(f => f.Id);

        foreach (var group in groups)
        {
            if (!fieldMap.TryGetValue(group.PrimaryFieldId, out var primary))
                continue;

            if (primary.IsManuallyGrouped) continue;

            primary.Role            = FieldRole.Primary;
            primary.GroupConfidence  = group.Confidence;
            primary.PrimaryGroupName = group.GroupName;

            var groupReferences = group.ReferenceFieldIds
                .Select(id => fieldMap.TryGetValue(id, out var m) ? m : null)
                .Where(m => m != null)
                .ToList();

            // ── Inherit flags ──
            InheritFlags(primary, groupReferences!);

            foreach (var reference in groupReferences)
            {
                if (reference!.IsManuallyGrouped) continue;

                reference.Role            = FieldRole.Reference;
                reference.ParentId        = primary.Id;
                reference.GroupConfidence  = group.Confidence;
                reference.PrimaryGroupName = group.GroupName;
            }
        }
    }

    /// <summary>
    /// Identifies potential primaries and sets their role, but does not link references yet.
    /// </summary>
    public void AutoSetPrimaries(List<AcroFieldModel> fields)
    {
        var groups = DetectGroups(fields);
        var fieldMap = fields.ToDictionary(f => f.Id);
        foreach (var group in groups)
        {
            if (fieldMap.TryGetValue(group.PrimaryFieldId, out var primary))
            {
                if (primary.IsManuallyGrouped) continue;
                primary.Role = FieldRole.Primary;
                primary.GroupConfidence = group.Confidence;
                primary.PrimaryGroupName = group.GroupName;
            }
        }
    }

    /// <summary>
    /// Links standalone fields to already identified primaries if they belong in a detected group.
    /// </summary>
    public void AutoAddReferences(List<AcroFieldModel> fields)
    {
        var groups = DetectGroups(fields);
        var fieldMap = fields.ToDictionary(f => f.Id);
        foreach (var group in groups)
        {
            if (!fieldMap.TryGetValue(group.PrimaryFieldId, out var primary)) continue;
            if (primary.Role != FieldRole.Primary) continue;

            foreach (var referenceId in group.ReferenceFieldIds)
            {
                if (fieldMap.TryGetValue(referenceId, out var reference))
                {
                    if (reference.Role == FieldRole.Standalone && !reference.IsManuallyGrouped)
                    {
                        reference.Role = FieldRole.Reference;
                        reference.ParentId = primary.Id;
                        reference.GroupConfidence = group.Confidence;
                        reference.PrimaryGroupName = group.GroupName;

                        // Inherit flags from the newly added reference
                        InheritFlags(primary, new[] { reference });
                    }
                }
            }
        }
    }

    private static void InheritFlags(AcroFieldModel primary, IEnumerable<AcroFieldModel> members)
    {
        foreach (var m in members)
        {
            // We only inherit Required if any child is required
            if (m.IsRequired) primary.IsRequired = true;
            
            // NOTE: We DON'T inherit IsReadOnly here anymore.
            // This allows the Primary to remain editable so that individual 
            // widget overrides (ReadOnly on page 4 but not on page 5) will work.
            
            if (m.IsMultiline) primary.IsMultiline = true;
            if (m.IsPassword)  primary.IsPassword  = true;
        }
    }

    // ── Heuristics

    /// <summary>
    /// Strips _copy, _ROWn, _ROWn_copy suffixes to derive the canonical base name.
    /// Examples:
    ///   DO1_copy          → DO1
    ///   DO1_ROW2_copy     → DO1_ROW2
    ///   Job Number_copy   → Job Number
    ///   DO1_ROW2          → DO1_ROW2  (ROW variants are separate fields)
    ///   DO1               → DO1
    /// </summary>
    internal static string ComputeBaseName(string fullName)
    {
        // Strip trailing _copy (case-insensitive)
        var result = CopySuffixRegex().Replace(fullName, string.Empty);
        return result;
    }

    [GeneratedRegex(@"_copy$", RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffixRegex();

    /// <summary>
    /// Elects the Primary field from a group of candidates.
    /// The one WITHOUT _copy suffix and with fewest widgets wins
    /// (it's the "original" the copies mirror).
    /// </summary>
    private static AcroFieldModel ElectPrimary(List<AcroFieldModel> members)
    {
        // Prefer the field that does NOT contain "_copy"
        var nonCopy = members
            .Where(m => !m.FullName.EndsWith("_copy", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nonCopy.Count == 1)
            return nonCopy[0];

        if (nonCopy.Count > 1)
        {
            // Multiple non-copy fields (shouldn't normally happen).
            // Pick the one with fewest widgets (most likely the original single instance).
            return nonCopy.OrderBy(m => m.Widgets.Count).First();
        }

        // All are copies — pick the one with fewest widgets
        return members.OrderBy(m => m.Widgets.Count).First();
    }

    private static float ScoreNameSimilarity(AcroFieldModel primary, List<AcroFieldModel> references, List<string> reasons)
    {
        // If all references' base names match the primary's base name, full score
        bool allMatch = references.All(k =>
            string.Equals(k.BaseName, primary.BaseName, StringComparison.OrdinalIgnoreCase));

        if (allMatch)
        {
            reasons.Add($"Name match: all fields share base name \"{primary.BaseName}\"");
            return NameWeight;
        }

        return 0f;
    }

    private static float ScoreRectangleMatch(AcroFieldModel primary, List<AcroFieldModel> references, List<string> reasons)
    {
        var primaryWidget = primary.Widgets.FirstOrDefault();
        if (primaryWidget == null) return 0f;

        int matchCount = references.Count(k =>
        {
            var kw = k.Widgets.FirstOrDefault();
            return kw != null &&
                   Math.Abs(kw.Width  - primaryWidget.Width)  < 0.5f &&
                   Math.Abs(kw.Height - primaryWidget.Height) < 0.5f;
        });

        float ratio = (float)matchCount / references.Count;
        if (ratio > 0.5f)
        {
            reasons.Add($"Rectangle match: {matchCount}/{references.Count} references same size " +
                         $"({primaryWidget.Width:0.#}×{primaryWidget.Height:0.#})");
            return RectWeight * ratio;
        }

        return 0f;
    }

    private static float ScoreFontMatch(AcroFieldModel primary, List<AcroFieldModel> references, List<string> reasons)
    {
        if (string.IsNullOrEmpty(primary.FontName)) return 0f;

        int matchCount = references.Count(k =>
            string.Equals(k.FontName, primary.FontName, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(k.FontSize - primary.FontSize) < 0.1f);

        float ratio = (float)matchCount / references.Count;
        if (ratio > 0.5f)
        {
            reasons.Add($"Font match: {matchCount}/{references.Count} references use " +
                         $"{primary.FontName} {primary.FontSize}pt");
            return FontWeight * ratio;
        }

        return 0f;
    }

    private static float ScoreMultiPage(List<AcroFieldModel> references, List<string> reasons)
    {
        int multiWidgetReferences = references.Count(k => k.Widgets.Count > 1);
        if (multiWidgetReferences > 0)
        {
            reasons.Add($"Multi-page: {multiWidgetReferences} reference(s) span multiple pages");
            return MultiPageWeight;
        }

        return 0f;
    }
}
