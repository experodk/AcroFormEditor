using Expero.AcroFormEditor.Models;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using iText.Layout.Properties;

namespace Expero.AcroFormEditor.Services;


file static class PdfStringExt
{
    internal static string ToStr(this PdfString? s, string fallback = "") => s is null ? fallback : s.ToUnicodeString();
}

public class AcroFormService
{
    // ── ISO 32000 field-flag bit values ──
    private const int FF_READ_ONLY = 1;
    private const int FF_REQUIRED = 1 << 1;
    private const int FF_MULTILINE = 1 << 12;
    private const int FF_PASSWORD = 1 << 13;
    private const int FF_RADIO = 1 << 15;
    private const int FF_PUSH_BUTTON = 1 << 16;
    private const int FF_COMBO = 1 << 17;
    private const int FF_FILE_SELECT = 1 << 20;
    private const int FF_DO_NOT_SPELL_CHECK = 1 << 22;
    private const int FF_DO_NOT_SCROLL = 1 << 23;
    private const int FF_COMB = 1 << 24;

    private const int FF_MANAGED_MASK = FF_READ_ONLY | FF_REQUIRED | FF_MULTILINE | FF_PASSWORD | FF_FILE_SELECT | FF_DO_NOT_SPELL_CHECK | FF_DO_NOT_SCROLL | FF_COMB;

    
    public List<AcroFieldModel> ExtractFields(Stream pdfStream)
    {
        var result = new List<AcroFieldModel>();

        using var reader = new PdfReader(pdfStream);
        using var doc = new PdfDocument(reader);
        var form = PdfAcroForm.GetAcroForm(doc, false);
        if (form == null) return result;

        foreach (var (name, field) in form.GetAllFormFields())
            result.Add(MapField(name, field, doc));

        return result;
    }

    private static AcroFieldModel MapField(string fullName, PdfFormField field, PdfDocument doc)
    {
        var model = new AcroFieldModel
        {
            FullName = fullName,
            PartialName = field.GetPartialFieldName().ToStr(fullName),
            AlternateName = field.GetAlternativeName().ToStr(),
            MappingName = field.GetMappingName().ToStr(),
            Value = field.GetValueAsString() ?? string.Empty,
            DefaultValue = (field.GetDefaultValue() as PdfString).ToStr(
                                field.GetDefaultValue()?.ToString() ?? string.Empty),
        };

        // ── Field type ──
        var ftName = field.GetFormType();
        if (ftName != null)
        {
            model.FieldTypeRaw = ftName.GetValue();
            int ff = field.GetFieldFlags();
            model.FieldType = model.FieldTypeRaw switch
            {
                "Tx" => AcroFieldType.Text,
                "Btn" => (ff & FF_RADIO) != 0 ? AcroFieldType.RadioButton
                       : (ff & FF_PUSH_BUTTON) != 0 ? AcroFieldType.Button
                       : AcroFieldType.CheckBox,
                "Ch" => (ff & FF_COMBO) != 0 ? AcroFieldType.ComboBox : AcroFieldType.ListBox,
                "Sig" => AcroFieldType.Signature,
                _ => AcroFieldType.Unknown
            };
        }

        // ── Flags ──
        int flags = field.GetFieldFlags();
        model.IsRequired = (flags & FF_REQUIRED) != 0;
        model.IsReadOnly = (flags & FF_READ_ONLY) != 0;
        model.IsMultiline = (flags & FF_MULTILINE) != 0;
        model.IsPassword = (flags & FF_PASSWORD) != 0;
        model.IsFileSelect = (flags & FF_FILE_SELECT) != 0;
        model.IsDoNotSpellCheck = (flags & FF_DO_NOT_SPELL_CHECK) != 0;
        model.IsDoNotScroll = (flags & FF_DO_NOT_SCROLL) != 0;
        model.IsComb = (flags & FF_COMB) != 0;

        // ── MaxLength ──
        var maxLen = field.GetPdfObject().GetAsNumber(PdfName.MaxLen);
        if (maxLen != null) model.MaxLength = (int)maxLen.GetValue();

        // ── Options ──
        var options = field.GetOptions();
        if (options != null)
        {
            for (int i = 0; i < options.Size(); i++)
            {
                var opt = options.GetAsArray(i);
                model.Options.Add(
                    opt != null && opt.Size() >= 2
                        ? opt.GetAsString(1).ToStr()
                        : options.GetAsString(i).ToStr());
            }
        }

        // ── Field-level DA (Default Appearance) ──
        var da = field.GetDefaultAppearance().ToStr();
        if (!string.IsNullOrWhiteSpace(da))
        {
            model.FontName = ExtractFontFromDA(da);
            model.FontSize = ExtractFontSizeFromDA(da);
        }

        // ── Justification ──
        model.Justification = field.GetJustification() switch
        {
            TextAlignment.CENTER => "Center",
            TextAlignment.RIGHT => "Right",
            _ => "Left"
        };

        // ── Widget placements – one entry per page occurrence ──
        // iText returns all widget annotations for the field (including those
        // coming from /Kids). Each widget has its own /Rect and page reference.
        var widgets = field.GetWidgets();
        if (widgets != null)
        {
            foreach (var widget in widgets)
            {
                var placement = new WidgetPlacement();

                var rectArr = widget.GetRectangle();
                if (rectArr != null)
                {
                    var rect = rectArr.ToRectangle();
                    placement.X = rect.GetX();
                    placement.Y = rect.GetY();
                    placement.Width = rect.GetWidth();
                    placement.Height = rect.GetHeight();
                }

                var page = widget.GetPage();
                if (page != null)
                    placement.PageNumber = doc.GetPageNumber(page);

                // ── Inherit or override appearance & flags ──
                
                // 1. DA (Font / Size / Color)
                var wda = widget.GetPdfObject().GetAsString(PdfName.DA)?.ToStr() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(wda))
                {
                    placement.FontName = ExtractFontFromDA(wda);
                    placement.FontSize = ExtractFontSizeFromDA(wda);
                    // TODO: Extract Color if needed
                }
                else
                {
                    placement.FontName = model.FontName;
                    placement.FontSize = model.FontSize;
                    placement.TextColor = model.TextColor;
                }

                // 2. Justification (/Q)
                var wq = widget.GetPdfObject().GetAsNumber(PdfName.Q);
                if (wq != null)
                {
                    placement.Justification = wq.IntValue() switch { 1 => "Center", 2 => "Right", _ => "Left" };
                }
                else
                {
                    placement.Justification = model.Justification;
                }

                // 3. ReadOnly (/F bit 7)
                int fFlags = widget.GetPdfObject().GetAsNumber(PdfName.F)?.IntValue() ?? 0;
                bool widgetReadOnly = (fFlags & 64) != 0;
                placement.IsReadOnly = widgetReadOnly || model.IsReadOnly;

                // 4. Border / Background (simplified inheritance for now)
                placement.BorderWidth = model.BorderWidth;
                placement.BorderColor = model.BorderColor;
                placement.BackgroundColor = model.BackgroundColor;

                model.Widgets.Add(placement);
            }
        }

        // Snapshot how many widgets this field had in the PDF at load time.
        // RebuildAcroForm uses this to distinguish pre-existing widgets from
        // new ones added during the editing session.
        model.OriginalWidgetCount = model.Widgets.Count;

        // ── Detect Role from PDF Hierarchy ──
        var pdfObj = field.GetPdfObject();
        var parent = pdfObj.GetAsDictionary(PdfName.Parent);
        var kidsArr = pdfObj.GetAsArray(PdfName.Kids);

        bool hasSubFields = false;
        if (kidsArr != null)
        {
            for (int i = 0; i < kidsArr.Size(); i++)
            {
                var kid = kidsArr.GetAsDictionary(i);
                // If a kid has a /T (Title), it's a sub-field, not just a widget
                if (kid != null && kid.ContainsKey(PdfName.T))
                {
                    hasSubFields = true;
                    break;
                }
            }
        }

        if (parent != null)
        {
            model.Role = FieldRole.Reference;
        }
        else if (hasSubFields || model.Widgets.Count > 1)
        {
            model.Role = FieldRole.Primary;
        }
        else
        {
            model.Role = FieldRole.Standalone;
        }

        return model;
    }

    private static string ExtractFontFromDA(string da)
    {
        var parts = da.Split(' ');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].StartsWith('/') &&
                parts[i + 1].Equals("Tf", StringComparison.OrdinalIgnoreCase))
                return parts[i].TrimStart('/');
        return string.Empty;
    }

    private static float ExtractFontSizeFromDA(string da)
    {
        var parts = da.Split(' ');
        for (int i = 1; i < parts.Length; i++)
            if (parts[i].Equals("Tf", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(parts[i - 1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var fs))
                return fs;
        return 0f;
    }

 
    /// <summary>
    /// Rebuilds the AcroForm in the PDF. Primary fields absorb all Kid widget
    /// placements so that filling the Primary once fills every placement.
    /// New widget placements added via the UI (beyond what was in the original
    /// PDF) are synthesised as real annotation dictionaries and wired up.
    /// Standalone fields are preserved as-is.
    /// </summary>
    public byte[] RebuildAcroForm(Stream originalPdfStream, List<AcroFieldModel> fields)
    {
        var originalBytes = ReadAllBytes(originalPdfStream);
        using var ms = new MemoryStream();

        using (var reader = new PdfReader(new MemoryStream(originalBytes)))
        using (var writer = new PdfWriter(ms))
        using (var doc = new PdfDocument(reader, writer))
        {
            var formDict = doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm);
            if (formDict == null) return ms.ToArray();

            // Instruct viewers to regenerate appearances based on the new values
            formDict.Put(PdfName.NeedAppearances, PdfBoolean.TRUE);
            formDict.SetModified();

            var fieldsArray = formDict.GetAsArray(PdfName.Fields);
            if (fieldsArray == null) return ms.ToArray();

            var deletedFields = fields.Where(f => f.IsDeleted).ToList();
            var activeFields = fields.Where(f => !f.IsDeleted).ToList();

            var primaries = activeFields.Where(f => f.Role == FieldRole.Primary).ToList();
            var kids = activeFields.Where(f => f.Role == FieldRole.Reference).ToList();
            var standalone = activeFields.Where(f => f.Role == FieldRole.Standalone).ToList();

            // Pre-scan all field dictionaries.
            // Key by model.FullName so every TryGetValue lookup below finds the right entry
            // even when FullName differs from the raw /T partial name in the PDF.
            var dictMap = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
            for (int i = 0; i < fieldsArray.Size(); i++)
            {
                var fDict = fieldsArray.GetAsDictionary(i);
                if (fDict == null) continue;

                var partialName = fDict.GetAsString(PdfName.T)?.GetValue();
                if (string.IsNullOrEmpty(partialName)) continue;

                foreach (var m in fields.Where(f =>
                    f.PartialName == partialName || f.FullName == partialName))
                {
                    dictMap[m.FullName] = fDict;
                }
            }

            // ── Process Deletions ──
            foreach (var del in deletedFields)
            {
                if (dictMap.TryGetValue(del.FullName, out var delDict))
                {
                    var kidKidsArray = delDict.GetAsArray(PdfName.Kids);
                    var widgetsToRemove = new List<PdfDictionary>();

                    if (kidKidsArray != null && kidKidsArray.Size() > 0)
                    {
                        for (int i = 0; i < kidKidsArray.Size(); i++)
                            widgetsToRemove.Add(kidKidsArray.GetAsDictionary(i));
                    }
                    else
                    {
                        widgetsToRemove.Add(delDict);
                    }

                    foreach (var w in widgetsToRemove)
                    {
                        var pageDict = w.GetAsDictionary(PdfName.P);
                        if (pageDict != null)
                        {
                            var page = doc.GetPage(pageDict);
                            var annots = page?.GetPdfObject().GetAsArray(PdfName.Annots);
                            if (annots != null)
                            {
                                for (int i = annots.Size() - 1; i >= 0; i--)
                                {
                                    var annot = annots.Get(i);
                                    if (annot == w || (annot.IsIndirect() && w.IsIndirect() &&
                                        annot.GetIndirectReference().GetObjNumber() == w.GetIndirectReference().GetObjNumber()))
                                    {
                                        annots.Remove(i);
                                        annots.SetModified();
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    fieldsArray.Remove(delDict);
                    fieldsArray.SetModified();
                }
            }

            var knownParentIds = primaries.Select(p => p.Id).ToHashSet();

            // ── Primary fields – merge existing Kid widgets, then synthesise new ones ──
            foreach (var primary in primaries)
            {
                if (!dictMap.TryGetValue(primary.FullName, out var primaryDict))
                    continue;

                // ── Dynamic ReadOnly Logic ──
                // To allow mixed states (some widgets locked, some editable), 
                // the logical field MUST be editable.
                // We only set the field to ReadOnly if EVERY widget in the group is ReadOnly.
                bool anyEditable = primary.Widgets.Any(w => w.IsReadOnly != true);
                var groupMembers = kids.Where(k => k.ParentId == primary.Id).ToList();
                bool effectiveReadOnly = primary.IsReadOnly;

                if (anyEditable || groupMembers.Any(k => k.Widgets.Any(w => w.IsReadOnly != true)))
                {
                    effectiveReadOnly = false;
                }
                else
                {
                    effectiveReadOnly = true;
                }

                UpdateFieldPropertiesDict(primaryDict, primary, effectiveReadOnly);
                EnsureNotMergedDict(doc, primaryDict);
                var primaryKidsArray = primaryDict.GetAsArray(PdfName.Kids)!;

                // ── Snapshot kid count BEFORE merging Reference fields ────────────
                // primary.Widgets is populated by ExtractFields via GetWidgets(), which
                // already traverses the full widget tree including kids. So
                // primary.Widgets already contains the widgets belonging to Reference
                // fields. We must count existing PDF kids NOW, before the merge loop
                // adds more, so the synthesis range [existingKidCount..primary.Widgets.Count)
                // only covers genuinely new UI-added placements.
                int existingKidCount = primaryKidsArray.Size();

                // Move widgets from Reference (kid) fields into this primary
                var primaryKids = kids.Where(k => k.ParentId == primary.Id).ToList();
                foreach (var kid in primaryKids)
                {
                    if (!dictMap.TryGetValue(kid.FullName, out var kidDict))
                        continue;

                    var kidKidsArray = kidDict.GetAsArray(PdfName.Kids);
                    var widgetsToMove = new List<PdfDictionary>();

                    if (kidKidsArray != null && kidKidsArray.Size() > 0)
                    {
                        for (int i = 0; i < kidKidsArray.Size(); i++)
                            widgetsToMove.Add(kidKidsArray.GetAsDictionary(i));
                    }
                    else
                    {
                        widgetsToMove.Add(kidDict);
                    }

                    for (int wi = 0; wi < widgetsToMove.Count; wi++)
                    {
                        var w = widgetsToMove[wi];
                        var placement = (wi < kid.Widgets.Count) ? kid.Widgets[wi] : null;

                        // Remove field-level properties from what is now strictly a widget annotation.
                        w.Remove(PdfName.T);
                        w.Remove(PdfName.V);
                        w.Remove(PdfName.DV);
                        w.Remove(PdfName.FT);
                        w.Remove(PdfName.Ff);
                        w.Remove(PdfName.TU);
                        w.Remove(PdfName.TM);
                        w.Remove(PdfName.Q);
                        w.Remove(PdfName.MaxLen);
                        w.Remove(PdfName.AA);
                        w.Remove(PdfName.Opt);

                        ApplyWidgetOverrides(w, placement, primary);
                        
                        w.Put(PdfName.Parent,
                            primaryDict.GetIndirectReference() ?? primaryDict.MakeIndirect(doc));
                        w.SetModified();
                        primaryKidsArray.Add(w);
                    }

                    primaryKidsArray.SetModified();
                    fieldsArray.Remove(kidDict);
                    fieldsArray.SetModified();
                }

                // ── Apply overrides to Primary's own existing widgets ──
                // These were already in the primaryKidsArray (or converted there).
                for (int wi = 0; wi < primary.OriginalWidgetCount; wi++)
                {
                    if (wi < primaryKidsArray.Size())
                    {
                        var w = primaryKidsArray.GetAsDictionary(wi);
                        if (wi < primary.Widgets.Count)
                        {
                            ApplyWidgetOverrides(w, primary.Widgets[wi], primary);
                        }
                    }
                }

                // ── Synthesise new widgets added to the Primary field via UI ──
                for (int wi = existingKidCount; wi < primary.Widgets.Count; wi++)
                    SynthesiseWidgetAnnotation(doc, primaryDict, primaryKidsArray, primary, primary.Widgets[wi]);

                // ── Synthesise new widgets added to Reference (kid) fields via UI ─
                // kid.OriginalWidgetCount = how many widgets the kid had in the PDF
                // (all of which were already moved above). Widgets[OriginalWidgetCount+]
                // are new placements added in this editing session.
                foreach (var kid in primaryKids)
                {
                    for (int wi = kid.OriginalWidgetCount; wi < kid.Widgets.Count; wi++)
                        SynthesiseWidgetAnnotation(doc, primaryDict, primaryKidsArray, kid, kid.Widgets[wi]);
                }

                // ── Remove widgets that were deleted from the model via UI ──
                // Build the full desired widget list: primary's own kept widgets +
                // all kept widgets from reference kids.
                var allKeptWidgets = primary.Widgets
                    .Concat(primaryKids.SelectMany(k => k.Widgets))
                    .ToList();
                RemoveDeletedWidgets(doc, primaryDict, primaryKidsArray, allKeptWidgets);

            }

            // ── Standalone / Orphaned Kids – update properties, synthesise new widgets ──
            var standaloneAndOrphans = standalone.Concat(
                kids.Where(k => string.IsNullOrEmpty(k.ParentId) || !knownParentIds.Contains(k.ParentId))
            );

            foreach (var f in standaloneAndOrphans)
            {
                if (!dictMap.TryGetValue(f.FullName, out var fDict))
                    continue;

                UpdateFieldPropertiesDict(fDict, f, f.IsReadOnly);

                // Only need to split/modify if widget count changed at all.
                if (f.Widgets.Count == f.OriginalWidgetCount && f.Widgets.Count <= 1)
                    continue;

                // Split merged field+widget dict so we can safely add/remove kids.
                EnsureNotMergedDict(doc, fDict);

                var kidsArray = fDict.GetAsArray(PdfName.Kids);
                if (kidsArray == null)
                    continue;

                // Synthesise new placements added via UI.
                for (int wi = 0; wi < f.OriginalWidgetCount; wi++)
                {
                    if (wi < kidsArray.Size())
                    {
                        var w = kidsArray.GetAsDictionary(wi);
                        if (wi < f.Widgets.Count)
                            ApplyWidgetOverrides(w, f.Widgets[wi], f);
                    }
                }

                for (int wi = f.OriginalWidgetCount; wi < f.Widgets.Count; wi++)
                    SynthesiseWidgetAnnotation(doc, fDict, kidsArray, f, f.Widgets[wi]);

                // Remove placements deleted via UI.
                RemoveDeletedWidgets(doc, fDict, kidsArray, f.Widgets);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Fills every editable text field with its own field name rendered in red.
    /// Useful as a visual preview to confirm which widget maps to which field.
    /// Non-text fields (checkboxes, buttons, signatures) are left untouched.
    /// Call after RebuildAcroForm so the widget layout reflects the current editor state.
    /// </summary>
    public byte[] FillWithFieldNames(Stream pdfStream)
    {
        var inputBytes = ReadAllBytes(pdfStream);
        using var ms = new MemoryStream();

        using (var reader = new PdfReader(new MemoryStream(inputBytes)))
        using (var writer = new PdfWriter(ms))
        using (var pdf = new PdfDocument(reader, writer))
        {
            var catalog = pdf.GetCatalog().GetPdfObject();
            var acroFormDict = catalog.GetAsDictionary(PdfName.AcroForm);
            if (acroFormDict == null) return inputBytes;

            acroFormDict.Put(PdfName.NeedAppearances, new PdfBoolean(true));

            var fieldsArray = acroFormDict.GetAsArray(PdfName.Fields);
            if (fieldsArray == null) return inputBytes;

            for (int i = 0; i < fieldsArray.Size(); i++)
            {
                var fieldDict = fieldsArray.GetAsDictionary(i);
                if (fieldDict == null) continue;

                var fieldName = fieldDict.GetAsString(PdfName.T)?.ToUnicodeString();
                if (string.IsNullOrEmpty(fieldName)) continue;

                // Skip buttons (Clear, Print)
                var ft = fieldDict.GetAsName(PdfName.FT);
                if (ft == null || ft.Equals(PdfName.Btn)) continue;

                // font size 0 = auto-fit, red color
                var da = new PdfString("/Helv 0 Tf 1 0 0 rg");

                fieldDict.Put(PdfName.V, new PdfString(fieldName));
                fieldDict.Put(PdfName.DA, da);
                fieldDict.Remove(PdfName.AP);

                var kids = fieldDict.GetAsArray(PdfName.Kids);
                if (kids == null) continue;

                for (int k = 0; k < kids.Size(); k++)
                {
                    var kid = kids.GetAsDictionary(k);
                    if (kid == null) continue;

                    kid.Put(PdfName.DA, da);
                    kid.Remove(PdfName.AP);
                }
            }
        }


        return ms.ToArray();
    }

    private static void CollectWidgetAnnotations(PdfDictionary dict, List<PdfDictionary> result)
    {
        var kids = dict.GetAsArray(PdfName.Kids);
        if (kids == null || kids.Size() == 0)
            return;

        for (int i = 0; i < kids.Size(); i++)
        {
            var kid = kids.GetAsDictionary(i);
            if (kid == null) continue;

            var subtype = kid.Get(PdfName.Subtype);
            if (PdfName.Widget.Equals(subtype) && kid.ContainsKey(PdfName.Rect))
            {
                result.Add(kid);
            }
            else
            {
                // Intermediate node — recurse
                CollectWidgetAnnotations(kid, result);
            }
        }
    }

    private void ApplyWidgetOverrides(PdfDictionary w, WidgetPlacement placement, AcroFieldModel parentModel)
    {
        if (w == null || placement == null) return;

        // ── Handle Overrides (Inherit from placement or parent model) ──
        string fontName = !string.IsNullOrWhiteSpace(placement.FontName) ? placement.FontName : (!string.IsNullOrWhiteSpace(parentModel.FontName) ? parentModel.FontName : "Helv");
        float fontSize = placement.FontSize > 0 ? placement.FontSize.Value : (parentModel.FontSize > 0 ? parentModel.FontSize : 10f);
        string justification = !string.IsNullOrWhiteSpace(placement.Justification) ? placement.Justification : parentModel.Justification;
        bool isReadOnly = placement.IsReadOnly ?? parentModel.IsReadOnly;

        // 1. Appearance Overrides (/DA)
        string textColor = !string.IsNullOrWhiteSpace(placement.TextColor) ? placement.TextColor : parentModel.TextColor;
        if (!string.IsNullOrEmpty(fontName) && (fontName != parentModel.FontName || Math.Abs(fontSize - parentModel.FontSize) > 0.01f || !string.IsNullOrWhiteSpace(placement.TextColor)))        
        {
            string fsStr = fontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string colorPart = HexToPdfColor(textColor ?? "#000000");
            string da = $"/{fontName} {fsStr} Tf {colorPart}";
            w.Put(PdfName.DA, new PdfString(da));
            
            // Force redraw to apply text color change
            w.Remove(PdfName.AP);
        }

        // 2. Justification Overrides (/Q)
        if (!string.IsNullOrEmpty(justification) && justification != parentModel.Justification)
        {
            int align = justification switch { "Center" => 1, "Right" => 2, _ => 0 };
            w.Put(PdfName.Q, new PdfNumber(align));
        }

        // 3. ReadOnly Override (/F bit 7)
        int currentF = w.GetAsNumber(PdfName.F)?.IntValue() ?? 4;
        if (isReadOnly) 
        {
            currentF |= 64; // Set ReadOnly bit in Annotation flags
            // Some readers also look for /Ff on the widget itself if it differs from parent
            w.Put(PdfName.Ff, new PdfNumber(1)); 
        }
        else 
        {
            currentF &= ~64; // Clear ReadOnly bit
            w.Remove(PdfName.Ff);
        }
        w.Put(PdfName.F, new PdfNumber(currentF));

        // 4. Color Overrides (/MK dictionary for BG and Border)
        if (!string.IsNullOrEmpty(placement.BackgroundColor) || !string.IsNullOrEmpty(placement.BorderColor))
        {
            var mk = w.GetAsDictionary(PdfName.MK) ?? new PdfDictionary();
            if (!string.IsNullOrEmpty(placement.BackgroundColor))
                mk.Put(PdfName.BG, ColorToPdfArray(placement.BackgroundColor));
            if (!string.IsNullOrEmpty(placement.BorderColor))
                mk.Put(PdfName.BC, ColorToPdfArray(placement.BorderColor));
            
            w.Put(PdfName.MK, mk);
            
            // CRITICAL: If we change colors/appearance, we MUST remove any existing
            // Appearance Stream (/AP) to force the reader to regenerate it.
            // Otherwise, it might display a stale, non-interactive image.
            w.Remove(PdfName.AP);
        }

        // 5. Border Width Override (/BS)
        if (placement.BorderWidth != null)
        {
            var bs = w.GetAsDictionary(PdfName.BS) ?? new PdfDictionary();
            bs.Put(PdfName.W, new PdfNumber(placement.BorderWidth.Value));
            w.Put(PdfName.BS, bs);
            w.Remove(PdfName.AP); // Force redraw
        }

        w.SetModified();
    }

    /// <summary>
    /// Creates a new widget annotation dictionary from a <see cref="WidgetPlacement"/>
    /// model, registers it as an indirect object, adds it to the page's /Annots array,
    /// and appends it to the field's /Kids array.
    /// </summary>
    private void SynthesiseWidgetAnnotation(PdfDocument doc, PdfDictionary fieldDict, PdfArray kidsArray, AcroFieldModel model, WidgetPlacement placement)
    {
        var page = doc.GetPage(placement.PageNumber);
        if (page == null) return;

        // Ensure the page is registered as an indirect object so we can store
        // a proper /P indirect reference in the widget annotation.
        var pageRef = page.GetPdfObject().GetIndirectReference()
                      ?? page.GetPdfObject().MakeIndirect(doc).GetIndirectReference();

        var newWidget = new PdfDictionary();
        newWidget.Put(PdfName.Type, PdfName.Annot);
        newWidget.Put(PdfName.Subtype, PdfName.Widget);

        // /FT is a field-level key; do NOT copy it onto the widget child.
        // It lives on the parent field dict only.

        newWidget.Put(PdfName.Rect, new PdfArray(new float[]
        {
            placement.X,
            placement.Y,
            placement.X + placement.Width,
            placement.Y + placement.Height
        }));

        // /P must be an indirect reference to the page, not an inline dict
        newWidget.Put(PdfName.P, pageRef);

        // Apply visual and behavior overrides
        ApplyWidgetOverrides(newWidget, placement, model);

        // Link widget → parent field (must be indirect ref)
        var fieldRef = fieldDict.GetIndirectReference()
                       ?? fieldDict.MakeIndirect(doc).GetIndirectReference();
        newWidget.Put(PdfName.Parent, fieldRef);

        // Register the widget as an indirect PDF object before adding anywhere
        var widgetRef = newWidget.MakeIndirect(doc);

        // Add to the page's /Annots array
        var pagePdfObj = page.GetPdfObject();
        var annots = pagePdfObj.GetAsArray(PdfName.Annots);
        if (annots == null)
        {
            annots = new PdfArray();
            pagePdfObj.Put(PdfName.Annots, annots);
        }
        annots.Add(widgetRef);
        annots.SetModified();
        pagePdfObj.SetModified();  // mark the page itself dirty so iText writes it

        // Add to the field's /Kids array
        kidsArray.Add(widgetRef);
        kidsArray.SetModified();
        // The field dict itself must also be marked dirty so iText serialises the updated /Kids
        fieldDict.SetModified();
    }

    /// <summary>
    /// Removes from the field's /Kids array (and the page's /Annots array) any widget
    /// that no longer has a corresponding entry in <paramref name="keptWidgets"/>.
    /// Matching is done by page number and /Rect coordinates (within a small tolerance).
    /// </summary>
    private static void RemoveDeletedWidgets(PdfDocument doc, PdfDictionary fieldDict, PdfArray kidsArray, List<WidgetPlacement> keptWidgets)
    {
        const float Tol = 1.0f; // coordinate tolerance in PDF units

        for (int i = kidsArray.Size() - 1; i >= 0; i--)
        {
            var kidDict = kidsArray.GetAsDictionary(i);
            if (kidDict == null) continue;

            // Determine the page number for this kid widget
            var pageObj = kidDict.GetAsDictionary(PdfName.P);
            int kidPage = pageObj != null ? doc.GetPageNumber(pageObj) : 0;

            // Get the /Rect of this kid
            var rectArr = kidDict.GetAsArray(PdfName.Rect);
            if (rectArr == null || rectArr.Size() < 4) continue;

            float kx = rectArr.GetAsNumber(0)?.FloatValue() ?? 0;
            float ky = rectArr.GetAsNumber(1)?.FloatValue() ?? 0;
            float kw = (rectArr.GetAsNumber(2)?.FloatValue() ?? 0) - kx;
            float kh = (rectArr.GetAsNumber(3)?.FloatValue() ?? 0) - ky;

            // Check if any kept widget matches this kid by page + rect
            bool isKept = keptWidgets.Any(w =>
                w.PageNumber == kidPage &&
                Math.Abs(w.X - kx) < Tol &&
                Math.Abs(w.Y - ky) < Tol &&
                Math.Abs(w.Width - kw) < Tol &&
                Math.Abs(w.Height - kh) < Tol);

            if (isKept) continue;

            // Remove from the page's /Annots array
            if (pageObj != null)
            {
                var page = doc.GetPage(pageObj);
                var annots = page?.GetPdfObject().GetAsArray(PdfName.Annots);
                if (annots != null)
                {
                    for (int a = annots.Size() - 1; a >= 0; a--)
                    {
                        var annot = annots.Get(a);
                        if (annot == kidDict ||
                           (annot.IsIndirect() && kidDict.IsIndirect() &&
                            annot.GetIndirectReference().GetObjNumber() ==
                            kidDict.GetIndirectReference().GetObjNumber()))
                        {
                            annots.Remove(a);
                            annots.SetModified();
                            page!.GetPdfObject().SetModified();
                            break;
                        }
                    }
                }
            }

            // Remove from /Kids
            kidsArray.Remove(i);
        }

        kidsArray.SetModified();
        fieldDict.SetModified();
    }

    /// <summary>
    /// Updates the properties of an existing PDF field based on the UI model.
    /// Preserves all other attributes (rotation, custom appearances, etc).
    /// </summary>
    private static void UpdateFieldPropertiesDict(PdfDictionary dict, AcroFieldModel model, bool? overrideReadOnly = null)
    {
        // ── Flags (PdfFormField Constants) ───
        int outFlags = 0;
        bool finalReadOnly = overrideReadOnly ?? model.IsReadOnly;
        if (finalReadOnly) outFlags |= FF_READ_ONLY;
        if (model.IsRequired) outFlags |= FF_REQUIRED;
        if (model.IsMultiline) outFlags |= FF_MULTILINE;
        if (model.IsPassword) outFlags |= FF_PASSWORD;
        if (model.IsFileSelect) outFlags |= FF_FILE_SELECT;
        if (model.IsDoNotSpellCheck) outFlags |= FF_DO_NOT_SPELL_CHECK;
        if (model.IsDoNotScroll) outFlags |= FF_DO_NOT_SCROLL;
        if (model.IsComb) outFlags |= FF_COMB;

        int currentFf = dict.GetAsNumber(PdfName.Ff)?.IntValue() ?? 0;
        int finalFf = (currentFf & ~FF_MANAGED_MASK) | outFlags;

        dict.Put(PdfName.Ff, new PdfNumber(finalFf));

        if (model.MaxLength > 0 && model.FieldType == AcroFieldType.Text)
            dict.Put(PdfName.MaxLen, new PdfNumber(model.MaxLength));

        if (!string.IsNullOrEmpty(model.Value))
            dict.Put(PdfName.V, new PdfString(model.Value, iText.IO.Font.PdfEncodings.UNICODE_BIG));

        if (!string.IsNullOrEmpty(model.AlternateName))
            dict.Put(PdfName.TU, new PdfString(model.AlternateName, iText.IO.Font.PdfEncodings.UNICODE_BIG));
        if (!string.IsNullOrEmpty(model.MappingName))
            dict.Put(PdfName.TM, new PdfString(model.MappingName, iText.IO.Font.PdfEncodings.UNICODE_BIG));

        int justAlign = model.Justification switch
        {
            "Center" => 1,
            "Right" => 2,
            _ => 0
        };
        dict.Put(PdfName.Q, new PdfNumber(justAlign));

        // ── Default Appearance (/DA) ──
        // Format: "/FontName FontSize Tf Color"
        if (!string.IsNullOrWhiteSpace(model.FontName))
        {
            string color = string.IsNullOrWhiteSpace(model.TextColor) ? "0 g" : model.TextColor;
            string fs = model.FontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string da = $"/{model.FontName} {fs} Tf {color}";
            dict.Put(PdfName.DA, new PdfString(da));
        }

        dict.SetModified();
    }

    /// <summary>
    /// Checks if a field dictionary is merged with a widget annotation dictionary.
    /// If it is, splits them into a parent Field and a child Widget to safely allow
    /// adding more kid widgets later.
    /// </summary>
    private static void EnsureNotMergedDict(PdfDocument doc, PdfDictionary dict)
    {
        if (dict.ContainsKey(PdfName.Subtype) && dict.Get(PdfName.Subtype).Equals(PdfName.Widget))
        {
            // Create a new dictionary for the widget
            var widgetDict = new PdfDictionary();

            // Keys that strictly belong to the widget annotation
            var widgetKeys = new[]
            {
                PdfName.Subtype, PdfName.Rect, PdfName.AP, PdfName.AS,
                PdfName.F, PdfName.MK, PdfName.P, PdfName.BS,
                PdfName.Border, PdfName.C, PdfName.A, PdfName.AA,
                PdfName.StructParent, PdfName.OC
            };

            foreach (var key in widgetKeys)
            {
                if (dict.ContainsKey(key))
                {
                    widgetDict.Put(key, dict.Get(key));
                    dict.Remove(key);
                }
            }

            // Link the new widget to the parent field
            widgetDict.Put(PdfName.Parent,
                dict.GetIndirectReference() ?? dict.MakeIndirect(doc));

            // Create a /Kids array in the parent field and add the new widget
            var kidsArray = new PdfArray();
            kidsArray.Add(widgetDict.MakeIndirect(doc));
            dict.Put(PdfName.Kids, kidsArray);
            dict.SetModified(); // mark dirty immediately after /Kids is added

            // Update the page's /Annots array to point to the new widget instead of the field
            if (widgetDict.ContainsKey(PdfName.P))
            {
                var pageDict = widgetDict.GetAsDictionary(PdfName.P);
                var page = doc.GetPage(pageDict);
                if (page != null)
                {
                    var annots = page.GetPdfObject().GetAsArray(PdfName.Annots);
                    if (annots != null)
                    {
                        for (int i = 0; i < annots.Size(); i++)
                        {
                            if (annots.Get(i) == dict ||
                               (annots.Get(i).IsIndirect() && dict.IsIndirect() &&
                                annots.Get(i).GetIndirectReference().GetObjNumber() ==
                                dict.GetIndirectReference().GetObjNumber()))
                            {
                                annots.Set(i, widgetDict);
                                break;
                            }
                        }
                    }
                }
            }

            dict.SetModified();
        }
    }

    private static string HexToPdfColor(string hex)
    {
        if (string.IsNullOrEmpty(hex) || !hex.StartsWith("#") || hex.Length < 7)
            return "0 g"; // Default black

        try
        {
            int r = int.Parse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber);
            int g = int.Parse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber);
            int b = int.Parse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber);
            
            // If R=G=B, use Gray color space (G) to save space, else use RGB (rg)
            if (r == g && g == b)
            {
                float gray = r / 255f;
                return $"{gray.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} g";
            }
            else
            {
                float rf = r / 255f;
                float gf = g / 255f;
                float bf = b / 255f;
                return $"{rf.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {gf.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {bf.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} rg";
            }
        }
        catch { return "0 g"; }
    }

    private static PdfArray ColorToPdfArray(string hexColor)
    {
        if (string.IsNullOrEmpty(hexColor) || !hexColor.StartsWith("#"))
            return new PdfArray(new float[] { 1, 1, 1 }); // Default White

        try
        {
            int r = int.Parse(hexColor.Substring(1, 2), System.Globalization.NumberStyles.HexNumber);
            int g = int.Parse(hexColor.Substring(3, 2), System.Globalization.NumberStyles.HexNumber);
            int b = int.Parse(hexColor.Substring(5, 2), System.Globalization.NumberStyles.HexNumber);
            return new PdfArray(new float[] { r / 255f, g / 255f, b / 255f });
        }
        catch { return new PdfArray(new float[] { 0, 0, 0 }); }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var buf = new MemoryStream();
        stream.CopyTo(buf);
        return buf.ToArray();
    }
}