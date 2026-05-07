namespace Expero.AcroFormEditor.Models;

/// <summary>
/// Represents a group of related fields where one Primary field
/// and its Kids will be merged into a single PDF field with
/// multiple widget annotations.
/// </summary>
public class FieldGroup
{
    /// <summary>Human-readable group name (typically the Primary's base name).</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Id of the field elected as Primary for this group.</summary>
    public string PrimaryFieldId { get; set; } = string.Empty;

    /// <summary>Ids of fields that are References (copies) in this group.</summary>
    public List<string> ReferenceFieldIds { get; set; } = new();

    /// <summary>Overall confidence score for this auto-detected group (0.0–1.0).</summary>
    public float Confidence { get; set; }

    /// <summary>Human-readable reasons why these fields were grouped together.</summary>
    public List<string> MatchReasons { get; set; } = new();
}
