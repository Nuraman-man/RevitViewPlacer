using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;

namespace RevitViewPlacer
{
    /// <summary>
    /// Создание и оформление Листа 1: Обложка (Титульный лист) альбома проекта.
    /// Использует семейство без штампа ("Лист_(Форма3)без_надписи.rfa"),
    /// размещает в правом нижнем углу крупным шрифтом имя автора и "Группа:",
    /// и оставляет поле листа чистым по требованию пользователя.
    /// </summary>
    public static class CoverSheetBuilder
    {
        private const double MmToFeet = 1.0 / 304.8;
        public const string CoverTitleBlockFamilyName = "Лист (Форма3) без надписи";
        public const string CoverTitleBlockFileName = "Лист_(Форма3)без_надписи.rfa";
        public const string EmbeddedCoverTitleBlockResourceName = "RevitViewPlacer.TitleBlockCover.rfa";

        public static FamilySymbol? FindOrLoadCoverTitleBlockSymbol(Document doc)
        {
            // 1. Поиск уже загруженного семейства в проекте
            FamilySymbol? symbol = FindSymbolInCoverFamily(doc);
            if (symbol != null)
            {
                if (!symbol.IsActive) symbol.Activate();
                return symbol;
            }

            // 2. Поиск и загрузка из файла на диске
            string? rfaPath = ResolveCoverTitleBlockRfaPath();
            if (!string.IsNullOrEmpty(rfaPath) && File.Exists(rfaPath))
            {
                try
                {
                    doc.LoadFamily(rfaPath, new CoverFamilyLoadOptions(), out _);
                    doc.Regenerate();
                }
                catch { }

                symbol = FindSymbolInCoverFamily(doc);
                if (symbol != null)
                {
                    if (!symbol.IsActive) symbol.Activate();
                    return symbol;
                }
            }

            return null;
        }

        private static FamilySymbol? FindSymbolInCoverFamily(Document doc)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .Where(tb => tb.FamilyName.Contains("без надписи", StringComparison.OrdinalIgnoreCase) ||
                             tb.FamilyName.Contains("без_надписи", StringComparison.OrdinalIgnoreCase) ||
                             tb.FamilyName.Contains("Обложк", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (symbols.Count == 0) return null;

            return symbols.FirstOrDefault(s => s.Name.Contains("А3", StringComparison.OrdinalIgnoreCase) ||
                                               s.Name.Contains("A3", StringComparison.OrdinalIgnoreCase))
                   ?? symbols[0];
        }

        public static string? ResolveCoverTitleBlockRfaPath()
        {
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string p1 = Path.Combine(asmDir, CoverTitleBlockFileName);
                if (File.Exists(p1)) return p1;

                string p2 = Path.Combine(asmDir, "Лист (Форма3) без надписи.rfa");
                if (File.Exists(p2)) return p2;

                string projDir = @"c:\Users\gygly\source\repos\RevitViewPlacer";
                string p3 = Path.Combine(projDir, CoverTitleBlockFileName);
                if (File.Exists(p3)) return p3;

                string p4 = Path.Combine(projDir, "Лист (Форма3) без надписи.rfa");
                if (File.Exists(p4)) return p4;

                // Извлечение из встроенного ресурса сборки
                Assembly asm = Assembly.GetExecutingAssembly();
                using Stream? stream = asm.GetManifestResourceStream(EmbeddedCoverTitleBlockResourceName);
                if (stream != null)
                {
                    string tempPath = Path.Combine(Path.GetTempPath(), CoverTitleBlockFileName);
                    using FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                    stream.CopyTo(fs);
                    return tempPath;
                }
            }
            catch { }
            return null;
        }

        public static ViewSheet CreateOrUpdateCoverSheet(
            Document doc,
            FamilySymbol coverSymbol,
            GostStampData gostStamp,
            int sheetSeqNumber,
            int totalSheetsCount,
            string sheetNumber)
        {
            ViewSheet? existingCover = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(s => !s.IsTemplate &&
                    (s.Name.Equals("Обложка", StringComparison.OrdinalIgnoreCase) ||
                     s.Name.Contains("Титульн", StringComparison.OrdinalIgnoreCase) ||
                     s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase)));

            ViewSheet sheet;
            if (existingCover != null && existingCover.GetAllViewports().Count == 0)
            {
                sheet = existingCover;
            }
            else
            {
                sheet = ViewSheet.Create(doc, coverSymbol.Id);
            }

            sheet.Name = "Обложка";
            sheet.SheetNumber = sheetNumber;

            // Назначаем семейство обложки
            Element? tbInst = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (tbInst == null)
            {
                doc.Create.NewFamilyInstance(XYZ.Zero, coverSymbol, sheet);
            }
            else if (tbInst.GetTypeId() != coverSymbol.Id)
            {
                try { tbInst.ChangeTypeId(coverSymbol.Id); } catch { }
            }

            GostStampHelper.ConfigureTitleBlockInstanceA3(doc, sheet);
            doc.Regenerate();

            // Очищаем любые старые детали и текст на обложке
            DeleteSheetDetailElements(doc, sheet);

            (double originXFt, double originYFt) = SheetLayoutEngine.GetSheetOriginFt(doc, sheet);

            // Крупный текст в правом нижнем углу по требованию пользователя:
            // "Имя которое ввели ранее в редактор и ниже Группа:(без ничего дальше уже сам заполняешь текст должен быть крупный)"
            ElementId largeTextTypeId = GetOrCreateLargeGostTextType(doc, 5.0, bold: true, "Обложка_5мм");

            string authorName = !string.IsNullOrWhiteSpace(gostStamp.FormattedDeveloperName)
                ? gostStamp.FormattedDeveloperName
                : (!string.IsNullOrWhiteSpace(gostStamp.RawUserInput) ? gostStamp.RawUserInput : "Турсынов И.Б.");

            string groupStr = !string.IsNullOrWhiteSpace(gostStamp.StudentGroup)
                ? $"Группа: {gostStamp.StudentGroup}"
                : "Группа:";

            string coverBottomRightText = $"{authorName}\n{groupStr}";

            // Правый нижний угол листа формата А3 (внутреннее поле: X до 415 мм, Y от 5 мм)
            // Размещаем блок от X = 265 мм до X = 412 мм, отметка Y = 45 мм
            XYZ textPos = new XYZ(
                originXFt + (265.0 * MmToFeet),
                originYFt + (45.0 * MmToFeet),
                0.0);

            TextNoteOptions opts = new TextNoteOptions(largeTextTypeId)
            {
                HorizontalAlignment = HorizontalTextAlignment.Left
            };

            TextNote note = TextNote.Create(doc, sheet.Id, textPos, 145.0 * MmToFeet, coverBottomRightText, opts);
            TagSheetElement(note);

            return sheet;
        }

        private static void DeleteSheetDetailElements(Document doc, ViewSheet sheet)
        {
            var detailIds = new FilteredElementCollector(doc, sheet.Id)
                .WherePasses(new ElementMulticategoryFilter([
                    BuiltInCategory.OST_Lines,
                    BuiltInCategory.OST_TextNotes
                ]))
                .WhereElementIsNotElementType()
                .Select(e => e.Id)
                .ToList();

            if (detailIds.Count > 0)
            {
                try { doc.Delete(detailIds); } catch { }
            }
        }

        private static void TagSheetElement(Element element)
        {
            try
            {
                Parameter? p = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p is { IsReadOnly: false })
                {
                    p.Set("ГП: Оформление листа ГОСТ");
                }
            }
            catch { }
        }

        private static ElementId GetOrCreateLargeGostTextType(Document doc, double heightMm, bool bold, string suffix)
        {
            string typeName = $"АР_ГОСТ_{suffix}_{heightMm:F1}мм";
            TextNoteType? existing = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

            if (existing != null) return existing.Id;

            TextNoteType? baseType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault();

            if (baseType == null) return ElementId.InvalidElementId;

            try
            {
                TextNoteType newType = (TextNoteType)baseType.Duplicate(typeName);
                newType.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(heightMm * MmToFeet);
                newType.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("ISOCPEUR");
                newType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.Set(bold ? 1 : 0);
                newType.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.Set(0.85);
                newType.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1); // Transparent
                return newType.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }

        private sealed class CoverFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Project;
                overwriteParameterValues = true;
                return true;
            }
        }
    }
}

