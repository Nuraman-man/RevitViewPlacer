using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfGrid = System.Windows.Controls.Grid;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace RevitViewPlacer
{
    /// <summary>
    /// Команда вызова мастера построения модели Revit по связанной или импортированной DWG-подложке.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GenerateFromDwgCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                PythonScriptRunnerCommand.EnsureAssemblyResolverRegistered();

                UIDocument uiDoc = commandData.Application.ActiveUIDocument;
                Document doc = uiDoc.Document;

                List<ImportInstance> dwgList = new FilteredElementCollector(doc)
                    .OfClass(typeof(ImportInstance))
                    .Cast<ImportInstance>()
                    .ToList();

                if (dwgList.Count == 0)
                {
                    TaskDialog.Show("Чертить по DWG", 
                        "В проекте не найдено импортированных или связанных чертежей DWG!\n\n" +
                        "Вставьте чертеж через вкладку:\n'Вставка' -> 'Связь САПР' или 'Импорт САПР' и повторите запуск.");
                    return Result.Cancelled;
                }

                DwgToBimWizardWindow window = new DwgToBimWizardWindow(commandData, dwgList);
                window.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Ошибка построения по DWG", ex.ToString());
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// Интерактивное WPF-окно мастера: выбор DWG, сопоставление слоев, высот и элементов.
    /// </summary>
    public class DwgToBimWizardWindow : Window
    {
        private readonly ExternalCommandData _commandData;
        private readonly List<ImportInstance> _dwgList;
        private readonly WpfComboBox _dwgComboBox;
        private readonly WpfComboBox _levelComboBox;
        private readonly WpfTextBox _wallHeightBox;
        private readonly WpfTextBox _logTextBox;
        private readonly TextBlock _statusTextBlock;

        // Настройки соответствия слоев (Layer Mapping)
        private readonly WpfTextBox _txtGridsLayer;
        private readonly WpfTextBox _txtWallsExtLayer;
        private readonly WpfTextBox _txtWallsIntLayer;
        private readonly WpfTextBox _txtCurtainLayer;
        private readonly WpfTextBox _txtDoorsLayer;
        private readonly WpfTextBox _txtWindowsLayer;
        private readonly WpfTextBox _txtOpeningsLayer;
        private readonly WpfTextBox _txtStairsLayer;
        private readonly WpfTextBox _txtRailingsLayer;

        // Чекбоксы включаемых категорий
        private readonly WpfCheckBox _chkGrids;
        private readonly WpfCheckBox _chkWallsExt;
        private readonly WpfCheckBox _chkWallsInt;
        private readonly WpfCheckBox _chkCurtains;
        private readonly WpfCheckBox _chkDoors;
        private readonly WpfCheckBox _chkWindows;
        private readonly WpfCheckBox _chkOpenings;
        private readonly WpfCheckBox _chkStairs;
        private readonly WpfCheckBox _chkRailings;
        private readonly WpfCheckBox _chkElevations;

        public DwgToBimWizardWindow(ExternalCommandData commandData, List<ImportInstance> dwgList)
        {
            _commandData = commandData;
            _dwgList = dwgList;

            Title = "Мастер поднятия BIM-модели по чертежу DWG";
            Width = 840;
            Height = 720;
            MinWidth = 680;
            MinHeight = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252));

            WpfGrid mainGrid = new WpfGrid { Margin = new Thickness(14) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Шапка
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: Основные параметры
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Сопоставление слоев
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: Выбор элементов
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4: Кнопка действия
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 5: Консоль логов

            // 1. Шапка
            Border header = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            StackPanel headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock
            {
                Text = "Автоматическое черчение здания по DWG-подложке",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14.5
            });
            headerStack.Children.Add(new TextBlock
            {
                Text = "Считывает слои, парные линии стен, проемы дверей и окон, ступени и ограждения.",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0)
            });
            header.Child = headerStack;
            WpfGrid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // 2. Блок выбора DWG, уровня и высот
            Border baseParamsBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            WpfGrid baseGrid = new WpfGrid();
            baseGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            baseGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            baseGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            baseGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            baseGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            baseGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Чертеж DWG
            TextBlock dwgLbl = new TextBlock { Text = "Чертеж DWG:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 3) };
            WpfGrid.SetRow(dwgLbl, 0); WpfGrid.SetColumn(dwgLbl, 0);
            baseGrid.Children.Add(dwgLbl);

            _dwgComboBox = new WpfComboBox { Height = 28, Margin = new Thickness(0, 3, 8, 3) };
            Document doc = _commandData.Application.ActiveUIDocument.Document;
            foreach (var dwg in _dwgList)
            {
                Element typeElem = doc.GetElement(dwg.GetTypeId());
                string name = typeElem != null ? typeElem.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? "DWG" : "DWG";
                _dwgComboBox.Items.Add(name + $" (Id: {dwg.Id})");
            }
            if (_dwgComboBox.Items.Count > 0) _dwgComboBox.SelectedIndex = 0;
            WpfGrid.SetRow(_dwgComboBox, 0); WpfGrid.SetColumn(_dwgComboBox, 1);
            baseGrid.Children.Add(_dwgComboBox);

            // Уровень
            TextBlock lvlLbl = new TextBlock { Text = "Уровень (этаж):", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 3) };
            WpfGrid.SetRow(lvlLbl, 1); WpfGrid.SetColumn(lvlLbl, 0);
            baseGrid.Children.Add(lvlLbl);

            _levelComboBox = new WpfComboBox { Height = 28, Margin = new Thickness(0, 3, 8, 3) };
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            foreach (var l in levels) _levelComboBox.Items.Add(l.Name);
            if (_levelComboBox.Items.Count > 0) _levelComboBox.SelectedIndex = 0;
            WpfGrid.SetRow(_levelComboBox, 1); WpfGrid.SetColumn(_levelComboBox, 1);
            baseGrid.Children.Add(_levelComboBox);

            // Высота стен
            TextBlock hLbl = new TextBlock { Text = "Высота стен (мм):", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 3, 0, 3) };
            WpfGrid.SetRow(hLbl, 0); WpfGrid.SetColumn(hLbl, 2);
            baseGrid.Children.Add(hLbl);

            _wallHeightBox = new WpfTextBox { Text = "3300", Height = 28, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
            WpfGrid.SetRow(_wallHeightBox, 0); WpfGrid.SetColumn(_wallHeightBox, 3);
            baseGrid.Children.Add(_wallHeightBox);

            baseParamsBorder.Child = baseGrid;
            WpfGrid.SetRow(baseParamsBorder, 1);
            mainGrid.Children.Add(baseParamsBorder);

            // 3. Блок настройки соответствия слоев (Layer Mapping)
            GroupBox layersGroup = new GroupBox
            {
                Header = " Сопоставление слоев AutoCAD (ключевые слова через запятую) ",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(8)
            };
            WpfGrid layerGrid = new WpfGrid();
            layerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            layerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            layerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            layerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _txtGridsLayer = AddLayerRow(layerGrid, 0, 0, "Оси:", "оси, grid, a-grid, axis", 0, 1);
            _txtWallsExtLayer = AddLayerRow(layerGrid, 0, 2, "Стены нар.:", "стены_наруж, wall_ext, a-wall-ext, капитальн", 0, 3);
            
            _txtWallsIntLayer = AddLayerRow(layerGrid, 1, 0, "Перегородки:", "перегородк, стены_внутр, wall_int, a-wall-int", 1, 1);
            _txtCurtainLayer = AddLayerRow(layerGrid, 1, 2, "Витражи:", "витраж, остеклен, curtain, glaz, a-glaz", 1, 3);

            _txtDoorsLayer = AddLayerRow(layerGrid, 2, 0, "Двери:", "двер, door, a-door, д-", 2, 1);
            _txtWindowsLayer = AddLayerRow(layerGrid, 2, 2, "Окна:", "окн, window, a-glaz-wind, ок-", 2, 3);

            _txtOpeningsLayer = AddLayerRow(layerGrid, 3, 0, "Проемы:", "проем, opening, арка, hole", 3, 1);
            _txtStairsLayer = AddLayerRow(layerGrid, 3, 2, "Ступени:", "лестниц, ступен, stair, a-flr-str", 3, 3);

            _txtRailingsLayer = AddLayerRow(layerGrid, 4, 0, "Ограждения:", "огражден, перил, railing, handrail", 4, 1);

            layersGroup.Content = layerGrid;
            WpfGrid.SetRow(layersGroup, 2);
            mainGrid.Children.Add(layersGroup);

            // 4. Чекбоксы создаваемых элементов
            Border chkBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 10)
            };
            WrapPanel chkWrap = new WrapPanel { Orientation = Orientation.Horizontal };

            _chkGrids = CreateCheckBox("Оси", true);
            _chkWallsExt = CreateCheckBox("Наружные стены", true);
            _chkWallsInt = CreateCheckBox("Перегородки", true);
            _chkCurtains = CreateCheckBox("Витражи", true);
            _chkDoors = CreateCheckBox("Двери", true);
            _chkWindows = CreateCheckBox("Окна", true);
            _chkOpenings = CreateCheckBox("Проемы", true);
            _chkStairs = CreateCheckBox("Ступени", true);
            _chkRailings = CreateCheckBox("Ограждения", true);
            _chkElevations = CreateCheckBox("4 Фасада", true);

            chkWrap.Children.Add(_chkGrids);
            chkWrap.Children.Add(_chkWallsExt);
            chkWrap.Children.Add(_chkWallsInt);
            chkWrap.Children.Add(_chkCurtains);
            chkWrap.Children.Add(_chkDoors);
            chkWrap.Children.Add(_chkWindows);
            chkWrap.Children.Add(_chkOpenings);
            chkWrap.Children.Add(_chkStairs);
            chkWrap.Children.Add(_chkRailings);
            chkWrap.Children.Add(_chkElevations);

            chkBorder.Child = chkWrap;
            WpfGrid.SetRow(chkBorder, 3);
            mainGrid.Children.Add(chkBorder);

            // 5. Кнопка действия
            DockPanel actionPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            WpfButton buildButton = new WpfButton
            {
                Content = "🚀 Начертить здание по DWG",
                Height = 36,
                Width = 260,
                FontWeight = FontWeights.Bold,
                FontSize = 13.5,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            buildButton.Click += (_, _) => RunBimGeneration();
            DockPanel.SetDock(buildButton, Dock.Left);
            actionPanel.Children.Add(buildButton);

            _statusTextBlock = new TextBlock
            {
                Text = "Готов к генерации.",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                FontSize = 12
            };
            DockPanel.SetDock(_statusTextBlock, Dock.Right);
            actionPanel.Children.Add(_statusTextBlock);

            WpfGrid.SetRow(actionPanel, 4);
            mainGrid.Children.Add(actionPanel);

            // 6. Поле вывода логов
            _logTextBox = new WpfTextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsReadOnly = true,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                Padding = new Thickness(8),
                Text = "Лог построения BIM-модели:\r\n"
            };
            WpfGrid.SetRow(_logTextBox, 5);
            mainGrid.Children.Add(_logTextBox);

            Content = mainGrid;

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.F5)
                {
                    e.Handled = true;
                    RunBimGeneration();
                }
            };
        }

        private WpfTextBox AddLayerRow(WpfGrid grid, int rowLbl, int colLbl, string label, string defaultValue, int rowBox, int colBox)
        {
            TextBlock lbl = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Margin = new Thickness(2, 2, 4, 2)
            };
            WpfGrid.SetRow(lbl, rowLbl);
            WpfGrid.SetColumn(lbl, colLbl);
            grid.Children.Add(lbl);

            WpfTextBox box = new WpfTextBox
            {
                Text = defaultValue,
                Height = 24,
                FontSize = 11.5,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, colBox == 1 ? 12 : 2, 2)
            };
            WpfGrid.SetRow(box, rowBox);
            WpfGrid.SetColumn(box, colBox);
            grid.Children.Add(box);
            return box;
        }

        private WpfCheckBox CreateCheckBox(string text, bool isChecked)
        {
            return new WpfCheckBox
            {
                Content = text,
                IsChecked = isChecked,
                FontWeight = FontWeights.Medium,
                FontSize = 12,
                Margin = new Thickness(0, 4, 14, 4)
            };
        }

        private void RunBimGeneration()
        {
            try
            {
                int selectedIndex = _dwgComboBox.SelectedIndex;
                if (selectedIndex < 0 || selectedIndex >= _dwgList.Count)
                {
                    AppendLog("[Ошибка] Не выбран чертеж DWG.");
                    return;
                }

                ImportInstance selectedDwg = _dwgList[selectedIndex];
                _statusTextBlock.Text = "Генерация элементов по DWG...";
                Stopwatch sw = Stopwatch.StartNew();

                string? pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string scriptPath = Path.Combine(pluginDir ?? "", "DwgToBimGenerator.py");

                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DwgToBimGenerator.py");
                }

                if (!File.Exists(scriptPath))
                {
                    AppendLog($"[Ошибка] Не найден скрипт генератора: {scriptPath}\r\n");
                    return;
                }

                string pythonCode = File.ReadAllText(scriptPath, Encoding.UTF8);

                ScriptEngine engine = Python.CreateEngine();
                engine.Runtime.LoadAssembly(typeof(Document).Assembly);
                engine.Runtime.LoadAssembly(typeof(UIApplication).Assembly);
                engine.Runtime.LoadAssembly(typeof(string).Assembly);
                engine.Runtime.LoadAssembly(typeof(System.Diagnostics.Process).Assembly);
                engine.Runtime.LoadAssembly(typeof(System.Linq.Enumerable).Assembly);

                using MemoryStream outStream = new MemoryStream();
                using MemoryStream errStream = new MemoryStream();
                Encoding utf8 = new UTF8Encoding(false);
                engine.Runtime.IO.SetOutput(outStream, utf8);
                engine.Runtime.IO.SetErrorOutput(errStream, utf8);

                ScriptScope scope = engine.CreateScope();
                UIApplication uiApp = _commandData.Application;
                UIDocument uiDoc = uiApp.ActiveUIDocument;
                Document doc = uiDoc.Document;

                scope.SetVariable("__revit__", uiApp);
                scope.SetVariable("uidoc", uiDoc);
                scope.SetVariable("doc", doc);
                scope.SetVariable("uiapp", uiApp);

                // Передаем кастомные маппинги слоев из UI
                var customMapping = new Dictionary<string, string[]>
                {
                    { "GRIDS", SplitKeywords(_txtGridsLayer.Text) },
                    { "WALLS_EXT", SplitKeywords(_txtWallsExtLayer.Text) },
                    { "WALLS_INT", SplitKeywords(_txtWallsIntLayer.Text) },
                    { "CURTAIN", SplitKeywords(_txtCurtainLayer.Text) },
                    { "DOORS", SplitKeywords(_txtDoorsLayer.Text) },
                    { "WINDOWS", SplitKeywords(_txtWindowsLayer.Text) },
                    { "OPENINGS", SplitKeywords(_txtOpeningsLayer.Text) },
                    { "STAIRS", SplitKeywords(_txtStairsLayer.Text) },
                    { "RAILINGS", SplitKeywords(_txtRailingsLayer.Text) }
                };
                scope.SetVariable("CUSTOM_LAYER_KEYWORDS", customMapping);

                AppendLog($"\r\n--- Запуск генерации [{DateTime.Now:HH:mm:ss}] ---");
                engine.Execute(pythonCode, scope);

                outStream.Position = 0;
                using StreamReader r = new StreamReader(outStream, Encoding.UTF8);
                string stdOut = r.ReadToEnd();
                if (!string.IsNullOrEmpty(stdOut)) AppendLog(stdOut);

                errStream.Position = 0;
                using StreamReader er = new StreamReader(errStream, Encoding.UTF8);
                string stdErr = er.ReadToEnd();
                if (!string.IsNullOrEmpty(stdErr)) AppendLog("[ОШИБКИ PYTHON]:\r\n" + stdErr);

                sw.Stop();
                _statusTextBlock.Text = $"Завершено за {sw.ElapsedMilliseconds} мс";
                AppendLog($"--- Готово за {sw.ElapsedMilliseconds} мс ---\r\n");
            }
            catch (Exception ex)
            {
                _statusTextBlock.Text = "Ошибка выполнения";
                AppendLog($"[ИСКЛЮЧЕНИЕ]: {ex.Message}\r\n{ex.StackTrace}\r\n");
            }
        }

        private string[] SplitKeywords(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim().ToLowerInvariant())
                      .ToArray();
        }

        private void AppendLog(string text)
        {
            _logTextBox.AppendText(text.EndsWith("\n") ? text : text + "\r\n");
            _logTextBox.ScrollToEnd();
        }
    }
}
