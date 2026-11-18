using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitViewPlacer
{
    /// <summary>
    /// Команда Revit для быстрого упорядочения и перенумерации всех помещений по ГОСТ 21.501-93 (1, 2, 3...).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RenumberRoomsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            RoomRenumberResult result;
            using (Transaction tx = new Transaction(doc, "Упорядочение нумерации помещений по ГОСТ"))
            {
                SheetLayoutEngine.ConfigureSilentTransaction(tx);
                tx.Start();

                result = RoomRenumberEngine.RenumberAllPlacedRooms(doc, RoomRenumberEngine.ModePerFloor);

                tx.Commit();
            }

            TaskDialog report = new TaskDialog("Нумерация помещений по ГОСТ")
            {
                MainInstruction = "Нумерация помещений успешно упорядочена!",
                MainContent = result.Message,
                CommonButtons = TaskDialogCommonButtons.Ok
            };
            report.Show();

            return Result.Succeeded;
        }
    }
}
