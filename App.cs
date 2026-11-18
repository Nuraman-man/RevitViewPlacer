using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitViewPlacer
{
    public class App : IExternalApplication
    {
        private const string TabName = "Авторазмещение АР";
        private const string PanelName = "Листы А3 и Генплан";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                PythonScriptRunnerCommand.EnsureAssemblyResolverRegistered();

                try
                {
                    application.CreateRibbonTab(TabName);
                }
                catch
                {
                    // Вкладка уже существует
                }

                RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                PushButtonData placeViewsBtnData = new PushButtonData(
                    name: "PlaceViewsButton",
                    text: "Разместить виды\nи экспликации",
                    assemblyName: assemblyPath,
                    className: typeof(PlaceViewsCommand).FullName!)
                {
                    ToolTip = "Загружает 'Лист (Форма3)' (А3_А), размещает все виды с экспликациями помещений и формирует лист генплана ГП-1."
                };

                PushButtonData sitePlanBtnData = new PushButtonData(
                    name: "GenerateDetailedSitePlanButton",
                    text: "Редактор ген-плана\nГП-1 (3D + Зоны)",
                    assemblyName: assemblyPath,
                    className: typeof(GenerateDetailedSitePlanCommand).FullName!)
                {
                    ToolTip = "Интерактивный редактор генерального плана: отрисовка границ участка, асфальтированных зон и проездов в маленьком окошке, настройка текстур и экспликация зон на листе ГП-1."
                };

                PushButtonData roomTagBtnData = new PushButtonData(
                    name: "ReplaceRoomTagsButton",
                    text: "Марки помещений\nADSK",
                    assemblyName: assemblyPath,
                    className: typeof(ReplaceRoomTagsCommand).FullName!)
                {
                    ToolTip = "Загружает семейство 'ADSK_Марка_Помещение_ДваЗначения' и заменяет все марки помещений в проекте (Номер Имя / Номер Площадь / Имя Площадь по ГОСТ)."
                };

                PushButtonData renumberRoomsBtnData = new PushButtonData(
                    name: "RenumberRoomsButton",
                    text: "Нумерация\n(ГОСТ 1, 2, 3...)",
                    assemblyName: assemblyPath,
                    className: typeof(RenumberRoomsCommand).FullName!)
                {
                    ToolTip = "Автоматически упорядочивает нумерацию помещений по этажам и по ходу движения от входа (1, 2, 3...) без пропусков и хаоса."
                };

                PushButtonData applyGostViewStyleBtnData = new PushButtonData(
                    name: "ApplyGostViewStyleButton",
                    text: "Оформить вид\n(Отметки + ГОСТ)",
                    assemblyName: assemblyPath,
                    className: typeof(ApplyGostViewStyleCommand).FullName!)
                {
                    ToolTip = "Оформляет активный вид по ГОСТ 21.501-93: расставляет высотные отметки и линию земли внутри вида, убирает мусор и настраивает подрезку."
                };

                PushButtonData generateFromDwgBtnData = new PushButtonData(
                    name: "GenerateFromDwgButton",
                    text: "Чертить по DWG\n(BIM Модель)",
                    assemblyName: assemblyPath,
                    className: typeof(GenerateFromDwgCommand).FullName!)
                {
                    ToolTip = "Мастер автоматического построения здания по подложке DWG: выбор слоев осей, стен, проемов, дверей, окон, ступеней и ограждений."
                };

                PushButtonData pythonRunnerBtnData = new PushButtonData(
                    name: "PythonScriptRunnerButton",
                    text: "Python Скрипт\n(pyRevit / API)",
                    assemblyName: assemblyPath,
                    className: typeof(PythonScriptRunnerCommand).FullName!)
                {
                    ToolTip = "Открывает интерактивную консоль IronPython для вставки и запуска Python-скриптов (формата pyRevit / RevitPythonShell) с глобальными переменными __revit__, uidoc и doc."
                };

                panel.AddItem(generateFromDwgBtnData);
                panel.AddItem(placeViewsBtnData);
                panel.AddItem(sitePlanBtnData);
                panel.AddItem(roomTagBtnData);
                panel.AddItem(renumberRoomsBtnData);
                panel.AddItem(applyGostViewStyleBtnData);
                panel.AddItem(pythonRunnerBtnData);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Ошибка загрузки плагина RevitViewPlacer", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
