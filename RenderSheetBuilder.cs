using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitViewPlacer
{
    /// <summary>
    /// Создание и оформление Листа визуализации (Лист 7 в комплекте).
    /// По требованию пользователя: "Визуализации пусть эти листы будут пустыми только всерху листа надпись Рендер".
    /// Использует стандартную рамку с основной надписью по ГОСТ (Форма 3),
    /// размещает сверху листа крупную надпись «РЕНДЕР», оставляя рабочее поле пустым для вставки рендеров.
    /// </summary>
    public static class RenderSheetBuilder
    {
        private const double MmToFeet = 1.0 / 304.8;

        public static ViewSheet CreateOrUpdateRenderSheet(
            Document doc,
            FamilySymbol titleBlockSymbol,
            GostStampData gostStamp,
            int sheetSeqNumber,
            int totalSheetsCount,
            string sheetNumber)
        {
            ViewSheet? existingRender = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .FirstOrDefault(s => !s.IsTemplate &&
                    (s.Name.Equals("Визуализация", StringComparison.OrdinalIgnoreCase) ||
                     s.Name.Equals("Рендер", StringComparison.OrdinalIgnoreCase) ||
                     s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase)));

            ViewSheet sheet;
            if (existingRender != null && existingRender.GetAllViewports().Count == 0)
            {
                sheet = existingRender;
            }
            else
            {
                sheet = ViewSheet.Create(doc, titleBlockSymbol.Id);
            }

            sheet.Name = "Визуализация";
            sheet.SheetNumber = sheetNumber;

            // Назначаем типовой штамп Форма 3
            Element? tbInst = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (tbInst == null)
            {
                doc.Create.NewFamilyInstance(XYZ.Zero, titleBlockSymbol, sheet);
            }
            else if (tbInst.GetTypeId() != titleBlockSymbol.Id)
            {
                try { tbInst.ChangeTypeId(titleBlockSymbol.Id); } catch { }
            }

            GostStampHelper.ConfigureTitleBlockInstanceA3(doc, sheet);
            doc.Regenerate();

            // Очищаем старые детали и текст на листе
            DeleteSheetDetailElements(doc, sheet);

            // Заполняем параметры штампа (шифр, название, стадию, номер листа),
            // текстовые строки фамилий в ячейки исключены для листов визуализации
            GostStampHelper.ApplyGostStampToSheet(doc, sheet, gostStamp, sheetSeqNumber, totalSheetsCount);
            doc.Regenerate();

            (double originXFt, double originYFt) = SheetLayoutEngine.GetSheetOriginFt(doc, sheet);

            // Сверху листа по центру размещаем крупную надпись «РЕНДЕР»
            ElementId renderTitleTypeId = GetOrCreateRenderTitleTextType(doc, 6.0, bold: true, "Рендер_6мм");

            // Лист А3: ширина 420 мм, отступ слева 20 мм, справа 5 мм (ширина рабочего поля 395 мм).
            // Точный центр рабочего поля листа: X = 20 + (395 / 2) = 217.5 мм
            // Верхнее поле 5 мм (Y = 292 мм) -> отметка центра заголовка Y = 282 мм
            XYZ titlePos = new XYZ(
                originXFt + (217.5 * MmToFeet),
                originYFt + (282.0 * MmToFeet),
                0.0);

            TextNoteOptions opts = new TextNoteOptions(renderTitleTypeId)
            {
                HorizontalAlignment = HorizontalTextAlignment.Center
            };

            TextNote titleNote = TextNote.Create(doc, sheet.Id, titlePos, "РЕНДЕР", opts);
            TagSheetElement(titleNote);

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

        private static ElementId GetOrCreateRenderTitleTextType(Document doc, double heightMm, bool bold, string suffix)
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
                newType.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.Set(0.9);
                newType.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1); // Transparent
                return newType.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }
    }
}
