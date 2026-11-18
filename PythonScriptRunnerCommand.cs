using System;
using System.Diagnostics;
using System.IO;
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
using WpfGrid = System.Windows.Controls.Grid;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace RevitViewPlacer
{
    /// <summary>
    /// Команда для запуска интерактивного окна выполнения Python-скриптов (формата pyRevit / RevitPythonShell)
    /// непосредственно в контексте Autodesk Revit API через движок IronPython.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PythonScriptRunnerCommand : IExternalCommand
    {
        private static bool _assemblyResolverRegistered;

        internal static void EnsureAssemblyResolverRegistered()
        {
            if (_assemblyResolverRegistered) return;
            _assemblyResolverRegistered = true;

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    AssemblyName requestedName = new AssemblyName(args.Name);
                    string? pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (string.IsNullOrEmpty(pluginDir)) return null;

                    string candidatePath = Path.Combine(pluginDir, requestedName.Name + ".dll");
                    if (File.Exists(candidatePath))
                    {
                        return Assembly.LoadFrom(candidatePath);
                    }
                }
                catch
                {
                    // Игнорируем ошибки разрешения сторонних сборок
                }
                return null;
            };
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                EnsureAssemblyResolverRegistered();

                ScriptRunnerWindow window = new ScriptRunnerWindow(commandData);
                window.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Ошибка запуска Python-консоли", ex.ToString());
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// WPF-окно редактора и консоли выполнения скриптов IronPython (pyRevit / RevitPythonShell).
    /// </summary>
    public class ScriptRunnerWindow : Window
    {
        private readonly ExternalCommandData _commandData;
        private readonly WpfTextBox _codeTextBox;
        private readonly WpfTextBox _outputTextBox;
        private readonly TextBlock _statusTextBlock;

        private const string DefaultStarterScript =
@"# Доступные глобальные переменные (совместимо с pyRevit / RevitPythonShell):
# __revit__ — объект UIApplication
# uidoc     — объект UIDocument (ActiveUIDocument)
# doc       — объект Document (ActiveUIDocument.Document)

import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
from Autodesk.Revit.DB import *
from Autodesk.Revit.UI import *

print('Документ проекта:', doc.Title)

walls = FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().ToElements()
levels = FilteredElementCollector(doc).OfClass(Level).ToElements()

print('Количество уровней в модели:', len(levels))
print('Количество стен в модели:', len(walls))

# Пример транзакции:
# t = Transaction(doc, 'Python Script Action')
# t.Start()
# ... ваш код изменения модели ...
# t.Commit()
";

        public ScriptRunnerWindow(ExternalCommandData commandData)
        {
            _commandData = commandData;

            Title = "Python Script Runner (IronPython / pyRevit / Revit API)";
            Width = 920;
            Height = 680;
            MinWidth = 640;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 244, 246));

            WpfGrid mainGrid = new WpfGrid
            {
                Margin = new Thickness(12)
            };

            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // Заголовок и подсказка
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) }); // Поле кода Python
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });                    // Сплиттер
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // Панель кнопок
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // Заголовок консоли
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) }); // Поле логов (Output)

            // 1. Верхняя панель с описанием переменных
            Border headerBorder = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            StackPanel headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock
            {
                Text = "Выполнение Python-скриптов (pyRevit / RevitPythonShell)",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14
            });
            headerStack.Children.Add(new TextBlock
            {
                Text = "Глобальные переменные:  __revit__ (UIApplication)   |   uidoc (UIDocument)   |   doc (Document)   |   Горячая клавиша: F5 или Ctrl+Enter",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0)
            });
            headerBorder.Child = headerStack;
            WpfGrid.SetRow(headerBorder, 0);
            mainGrid.Children.Add(headerBorder);

            // 2. Многострочное текстовое поле для Python-кода
            _codeTextBox = new WpfTextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13.5,
                AcceptsReturn = true,
                AcceptsTab = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                Padding = new Thickness(10),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)),
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
                BorderThickness = new Thickness(1),
                Text = DefaultStarterScript
            };
            WpfGrid.SetRow(_codeTextBox, 1);
            mainGrid.Children.Add(_codeTextBox);

            // 3. Разделитель (GridSplitter)
            GridSplitter splitter = new GridSplitter
            {
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                ShowsPreview = true
            };
            WpfGrid.SetRow(splitter, 2);
            mainGrid.Children.Add(splitter);

            // 4. Панель кнопок («Запустить скрипт» и «Очистить»)
            DockPanel buttonBar = new DockPanel
            {
                Margin = new Thickness(0, 4, 0, 6)
            };

            StackPanel buttonsLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };

            Button runButton = new Button
            {
                Content = "▶ Запустить скрипт (F5)",
                Width = 190,
                Height = 34,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 0)
            };
            runButton.Click += (_, _) => ExecutePythonScript();

            Button clearOutputButton = new Button
            {
                Content = "Очистить",
                Width = 120,
                Height = 34,
                FontSize = 13,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 0)
            };
            clearOutputButton.Click += (_, _) =>
            {
                _outputTextBox?.Clear();
                if (_statusTextBlock != null)
                {
                    _statusTextBlock.Text = "Консоль очищена.";
                }
            };

            Button clearCodeButton = new Button
            {
                Content = "Очистить код",
                Width = 120,
                Height = 34,
                FontSize = 13,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            clearCodeButton.Click += (_, _) =>
            {
                _codeTextBox.Clear();
                _codeTextBox.Focus();
            };

            buttonsLeft.Children.Add(runButton);
            buttonsLeft.Children.Add(clearOutputButton);
            buttonsLeft.Children.Add(clearCodeButton);
            DockPanel.SetDock(buttonsLeft, Dock.Left);
            buttonBar.Children.Add(buttonsLeft);

            _statusTextBlock = new TextBlock
            {
                Text = "Готов к выполнению.",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                FontSize = 12
            };
            DockPanel.SetDock(_statusTextBlock, Dock.Right);
            buttonBar.Children.Add(_statusTextBlock);

            WpfGrid.SetRow(buttonBar, 3);
            mainGrid.Children.Add(buttonBar);

            // 5. Заголовок секции вывода
            TextBlock outputLabel = new TextBlock
            {
                Text = "Output / Консоль вывода (print и стек-трейсы ошибок):",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            WpfGrid.SetRow(outputLabel, 4);
            mainGrid.Children.Add(outputLabel);

            // 6. Поле вывода логов / ошибок (Output / Консоль) в нижней части окна
            _outputTextBox = new WpfTextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                IsReadOnly = true,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(10),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
                BorderThickness = new Thickness(1)
            };
            WpfGrid.SetRow(_outputTextBox, 5);
            mainGrid.Children.Add(_outputTextBox);

            Content = mainGrid;

            // Горячие клавиши F5 и Ctrl+Enter для быстрого запуска скрипта
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.F5 || (e.Key == Key.Return && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
                {
                    e.Handled = true;
                    ExecutePythonScript();
                }
            };
        }

        private void ExecutePythonScript()
        {
            string code = _codeTextBox.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                AppendOutput("[Предупреждение] Поле кода пустое. Вставьте Python-скрипт для выполнения.\r\n");
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            _statusTextBlock.Text = "Выполнение скрипта...";

            using MemoryStream outputStream = new MemoryStream();
            using MemoryStream errorStream = new MemoryStream();

            ScriptEngine? engine = null;

            try
            {
                engine = Python.CreateEngine();

                // 1. Загружаем в runtime сборки RevitAPI, RevitAPIUI и стандартную библиотеку System
                engine.Runtime.LoadAssembly(typeof(Document).Assembly);
                engine.Runtime.LoadAssembly(typeof(UIApplication).Assembly);
                engine.Runtime.LoadAssembly(typeof(string).Assembly);
                engine.Runtime.LoadAssembly(typeof(System.Diagnostics.Process).Assembly);
                engine.Runtime.LoadAssembly(typeof(System.Linq.Enumerable).Assembly);

                // 2. Перенаправляем стандартный вывод и поток ошибок в MemoryStream
                Encoding utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                engine.Runtime.IO.SetOutput(outputStream, utf8NoBom);
                engine.Runtime.IO.SetErrorOutput(errorStream, utf8NoBom);

                // 3. Создаем ScriptScope и пробрасываем глобальные переменные совместимости с pyRevit
                ScriptScope scope = engine.CreateScope();
                UIApplication uiApp = _commandData.Application;
                UIDocument? uiDoc = uiApp.ActiveUIDocument;
                Document? doc = uiDoc?.Document;

                scope.SetVariable("__revit__", uiApp);
                scope.SetVariable("uidoc", uiDoc);
                scope.SetVariable("doc", doc);
                scope.SetVariable("uiapp", uiApp);
                scope.SetVariable("__commandData__", _commandData);

                AppendOutput($"--- Запуск скрипта [{DateTime.Now:HH:mm:ss}] ---\r\n");

                // 4. Выполняем Python-код в текущем контексте Revit API
                engine.Execute(code, scope);

                FlushStreamsToOutput(outputStream, errorStream);

                sw.Stop();
                _statusTextBlock.Text = $"Успешно выполнено за {sw.ElapsedMilliseconds} мс";
                AppendOutput($"--- Завершено успешно ({sw.ElapsedMilliseconds} мс) ---\r\n\r\n");
            }
            catch (Exception ex)
            {
                sw.Stop();
                FlushStreamsToOutput(outputStream, errorStream);

                string formattedException;
                try
                {
                    ExceptionOperations? eo = engine?.GetService<ExceptionOperations>();
                    formattedException = eo != null ? eo.FormatException(ex) : ex.ToString();
                }
                catch
                {
                    formattedException = ex.ToString();
                }

                _statusTextBlock.Text = "Ошибка выполнения скрипта";
                AppendOutput($"[ОШИБКА ИСПОЛНЕНИЯ PYTHON]:\r\n{formattedException}\r\n");
                AppendOutput($"--- Прервано с ошибкой ({sw.ElapsedMilliseconds} мс) ---\r\n\r\n");
            }
        }

        private void FlushStreamsToOutput(MemoryStream outputStream, MemoryStream errorStream)
        {
            if (outputStream.Length > 0)
            {
                outputStream.Position = 0;
                using StreamReader reader = new StreamReader(outputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                string stdOut = reader.ReadToEnd();
                if (!string.IsNullOrEmpty(stdOut))
                {
                    AppendOutput(stdOut.EndsWith("\n") ? stdOut : stdOut + "\r\n");
                }
                outputStream.SetLength(0);
            }

            if (errorStream.Length > 0)
            {
                errorStream.Position = 0;
                using StreamReader errReader = new StreamReader(errorStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                string stdErr = errReader.ReadToEnd();
                if (!string.IsNullOrEmpty(stdErr))
                {
                    AppendOutput("[STDERR]:\r\n" + (stdErr.EndsWith("\n") ? stdErr : stdErr + "\r\n"));
                }
                errorStream.SetLength(0);
            }
        }

        private void AppendOutput(string text)
        {
            _outputTextBox.AppendText(text);
            _outputTextBox.ScrollToEnd();
        }
    }
}
