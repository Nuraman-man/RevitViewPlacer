using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitViewPlacer
{
    /// <summary>
    /// Команда Revit для немедленного оформления текущего активного вида (фасада, разреза или плана)
    /// по ГОСТ 21.501-93 / ГОСТ 21.101-97:
    /// расстановка высотных отметок и линии земли ВНУТРИ вида, очистка графики и настройка CropBox.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ApplyGostViewStyleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (activeView is ViewSheet)
            {
                TaskDialog.Show(
                    "Оформление вида по ГОСТ",
                    "Текущий активный вид — Лист.\n\n" +
                    "Переключитесь на фасад, разрез или план этажа в Диспетчере проекта и запустите команду повторно.");
                return Result.Cancelled;
            }

            if (activeView.ViewType is not (ViewType.Elevation or ViewType.Section or ViewType.FloorPlan or ViewType.CeilingPlan))
            {
                TaskDialog.Show(
                    "Оформление вида по ГОСТ",
                    $"Вид «{activeView.Name}» ({activeView.ViewType}) не является фасадом, разрезом или планом.\n" +
                    "Команда предназначена для фасадов, разрезов и планов этажей.");
                return Result.Cancelled;
            }

            using (Transaction tx = new Transaction(doc, "Оформление вида по ГОСТ РК"))
            {
                SheetLayoutEngine.ConfigureSilentTransaction(tx);
                tx.Start();

                ViewGostStyler.PrepareArchitecturalViewForGost(doc, activeView, null);

                tx.Commit();
            }

            string typeDesc = activeView.ViewType switch
            {
                ViewType.Elevation => "фасада",
                ViewType.Section => "разреза",
                _ => "плана"
            };

            TaskDialog report = new TaskDialog("Оформление вида по ГОСТ")
            {
                MainInstruction = $"Оформление {typeDesc} «{activeView.Name}» выполнено!",
                MainContent =
                    "Вид приведен в соответствие с ГОСТ 21.501-93, ГОСТ 21.101-97 и СН РК:\n\n" +
                    " • Установлен стиль «Скрытые линии» (HLR) и высокая детализация\n" +
                    " • Скрыты базовые точки, опорные плоскости, камеры и элементы генплана\n" +
                    (activeView.ViewType is ViewType.Elevation or ViewType.Section
                        ? " • Скрыты координационные оси и размеры (по ГОСТ исключены с фасадов и разрезов)\n" +
                          " • ВНУТРИ вида нанесена толстая линия земли со штриховкой грунта 45°\n" +
                          " • ВНУТРИ вида расставлены высотные отметки ГОСТ строго в строчку вдоль здания (без учета генплана)\n" +
                          (activeView.ViewType == ViewType.Section
                              ? " • Для разреза: нанесены внутренние отметки перекрытий (±0.000) и этажерки пирогов кровли и пола по ГОСТ 21.501\n"
                              : "")
                        : "") +
                    " • Настроена подрезка CropBox (рамка скрыта для чистоты листа)",
                CommonButtons = TaskDialogCommonButtons.Ok
            };
            report.Show();

            return Result.Succeeded;
        }
    }
}
