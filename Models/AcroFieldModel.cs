namespace Expero.AcroFormEditor.Models;

public enum FieldRole
{
    Standalone,   // Independent field
    Primary,      // Parent field – owns one or more placements from References
    Reference     // Field that will be absorbed as a placement into a Primary
}

public enum AcroFieldType
{
    Unknown,
    Text,
    CheckBox,
    RadioButton,
    Button,
    ListBox,
    ComboBox,
    Signature
}

/// <summary>
/// Represents a single widget annotation placement for a field.
/// A field can have one widget (simple case) or many widgets across
/// different pages (e.g. a header field repeated on every page).
/// </summary>
public class WidgetPlacement
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Which page this widget lives on (1-based)
    public int PageNumber { get; set; }

    // Position on the page (PDF coordinate system: origin bottom-left)
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    // Widget-level appearance (can override field-level DA)
    public string? FontName { get; set; }
    public float? FontSize { get; set; }
    public string? TextColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BorderColor { get; set; }
    public int? BorderWidth { get; set; }
    public string? Justification { get; set; }
    public bool? IsReadOnly { get; set; }

    // Display helper
    public bool IsExpanded { get; set; } = false;

    public override string ToString() =>
        $"p{PageNumber}  ({X:0.#}, {Y:0.#})  {Width:0.#} × {Height:0.#}";
}

/// <summary>
/// Represents one AcroForm logical field (the /T dictionary entry and all
/// its shared properties), together with all its widget placements.
/// </summary>
public class AcroFieldModel
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FullName { get; set; } = string.Empty;
    public string PartialName { get; set; } = string.Empty;
    public string AlternateName { get; set; } = string.Empty;
    public string MappingName { get; set; } = string.Empty;

    // ── Type & Value ──────────────────────────────────────────────────────────
    public AcroFieldType FieldType { get; set; }
    public string FieldTypeRaw { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();

    // ── Widget placements (one per page occurrence) ───────────────────────────
    // A field with /Kids in the PDF has multiple widget annotations;
    // each one maps to an entry in this list.
    public List<WidgetPlacement> Widgets { get; set; } = new();

    /// <summary>
    /// Number of widget placements this field had when it was first extracted
    /// from the PDF. Used by RebuildAcroForm to distinguish pre-existing widgets
    /// from new ones added in the current editing session.
    /// Never modified after ExtractFields sets it.
    /// </summary>
    public int OriginalWidgetCount { get; set; }

    // Convenience: pages this field appears on (derived from Widgets)
    public string PageSummary =>
        Widgets.Count == 0 ? "–" :
        Widgets.Count == 1 ? $"p{Widgets[0].PageNumber}" :
        $"p{Widgets[0].PageNumber} +{Widgets.Count - 1} more";

    // ── Flags (shared across all widgets) ────────────────────────────────────
    public bool IsRequired { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsMultiline { get; set; }
    public bool IsPassword { get; set; }
    public bool IsFileSelect { get; set; }
    public bool IsDoNotSpellCheck { get; set; }
    public bool IsDoNotScroll { get; set; }
    public bool IsComb { get; set; }
    public int MaxLength { get; set; }

    // ── Appearance (field-level /DA – shared default) ─────────────────────────
    public string FontName { get; set; } = string.Empty;
    public float FontSize { get; set; }
    public string TextColor { get; set; } = string.Empty;
    public string BackgroundColor { get; set; } = string.Empty;
    public string BorderColor { get; set; } = string.Empty;
    public int BorderWidth { get; set; }
    public string Justification { get; set; } = string.Empty;

    // ── Hierarchy editing ─────────────────────────────────────────────────────
    public FieldRole Role { get; set; } = FieldRole.Standalone;
    public string? ParentId { get; set; }   // Set when Role == Reference
    public string PrimaryGroupName { get; set; } = string.Empty;

    // ── Auto-grouping metadata ────────────────────────────────────────────────

    /// <summary>Base name derived by stripping _copy / _ROW2_copy suffixes.</summary>
    public string BaseName { get; set; } = string.Empty;

    // ── Deletion ──────────────────────────────────────────────────────────────
    public bool IsDeleted { get; set; }

    /// <summary>Auto-grouping confidence score (0.0–1.0). Null if manually assigned.</summary>
    public float? GroupConfidence { get; set; }

    /// <summary>Whether the user has manually confirmed or overridden the auto-grouping.</summary>
    public bool IsManuallyGrouped { get; set; }

    // ── Display helpers ───────────────────────────────────────────────────────
    public bool IsExpanded { get; set; } = false;
    public bool IsSelected { get; set; } = false;
}