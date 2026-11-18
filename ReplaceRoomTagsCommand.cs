using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WpfGrid = System.Windows.Controls.Grid;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace RevitViewPlacer
{
    /// <summary>
    /// Команда Revit для быстрой замены марок помещений в проекте или на виде
    /// на семейство "ADSK_Марка_Помещение_ДваЗначения".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ReplaceRoomTagsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            View activeView = doc.ActiveView;

            // Показываем окно настроек замены марок
            ReplaceRoomTagsWindow window = new ReplaceRoomTagsWindow(activeView.Name);
            bool? dialogRes = window.ShowDialog();
            if (dialogRes != true)
            {
                return Result.Cancelled;
            }

            string chosenType = window.SelectedTypeName;
            bool onlyActiveView = window.OnlyActiveView;
            bool tagUntagged = window.TagUntaggedRooms;
            bool renumberRooms = window.RenumberRooms;

            List<View>? targetViews = null;
            if (onlyActiveView)
            {
                if (activeView.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan))
                {
                    TaskDialog.Show(
                        "Замена марок помещений",
                        "Текущий активный вид («" + activeView.Name + "») не является планом этажа или потолка.\n" +
                        "Переключитесь на план этажа или выберите область «Во всем проекте».");
                    return Result.Failed;
                }
                targetViews = [activeView];
            }

            RoomTagReplacementResult result;
            RoomRenumberResult? renumResult = null;
            using (Transaction tx = new Transaction(doc, "Замена марок помещений на ADSK и нумерация"))
            {
                SheetLayoutEngine.ConfigureSilentTransaction(tx);
                tx.Start();

                if (renumberRooms)
                {
                    renumResult = RoomRenumberEngine.RenumberAllPlacedRooms(doc);
                }

                result = RoomTagManager.ReplaceOrPlaceRoomTags(
                    doc,
                    targetViews,
                    preferredTypeName: chosenType,
                    tagUntaggedRooms: tagUntagged);

                tx.Commit();
            }

            string renumText = renumResult != null
                ? $"• Нумерация помещений: упорядочено {renumResult.RenumberedCount} помещений (1, 2, 3...)\n"
                : string.Empty;

            TaskDialog reportDialog = new TaskDialog("Марки помещений ADSK")
            {
                MainInstruction = "Марки помещений и нумерация успешно обновлены!",
                MainContent =
                    $"Семейство: {result.FamilyName}\n" +
                    $"Типоразмер: {result.TypeName}\n\n" +
                    renumText +
                    $"• Заменено существующих марок: {result.ReplacedCount} шт.\n" +
                    (tagUntagged ? $"• Размещено новых марок для неразмеченных помещений: {result.CreatedCount} шт.\n" : "") +
                    (onlyActiveView ? $"• Область: только вид «{activeView.Name}»" : "• Область: все планы этажей проекта"),
                CommonButtons = TaskDialogCommonButtons.Ok
            };
            reportDialog.Show();

            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Диалоговое окно WPF для выбора параметров замены марок помещений на ADSK.
    /// </summary>
    internal sealed class ReplaceRoomTagsWindow : Window
    {
        private readonly WpfComboBox _typeComboBox;
        private readonly RadioButton _allViewsRadio;
        private readonly RadioButton _activeViewRadio;
        private readonly CheckBox _tagUntaggedCheckBox;
        private readonly CheckBox _renumberCheckBox;

        public string SelectedTypeName => (_typeComboBox.SelectedItem as string) ?? "Номер Имя";
        public bool OnlyActiveView => _activeViewRadio.IsChecked == true;
        public bool TagUntaggedRooms => _tagUntaggedCheckBox.IsChecked == true;
        public bool RenumberRooms => _renumberCheckBox.IsChecked == true;

        public ReplaceRoomTagsWindow(string activeViewName)
        {
            Title = "Марки помещений ADSK (ГОСТ РК)";
            Width = 490;
            Height = 410;
            MinWidth = 420;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252));
            FontFamily = new FontFamily("Segoe UI");

            Border rootBorder = new Border
            {
                Padding = new Thickness(20, 16, 20, 16)
            };

            WpfGrid rootGrid = new WpfGrid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel contentStack = new StackPanel();

            TextBlock titleBlock = new TextBlock
            {
                Text = "Замена марок помещений на ADSK",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            contentStack.Children.Add(titleBlock);

            TextBlock descBlock = new TextBlock
            {
                Text = "Загружает стандартное семейство «ADSK_Марка_Помещение_ДваЗначения» и обновляет марки помещений в соответствии с ГОСТ 21.501-93 и СН РК.",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            contentStack.Children.Add(descBlock);

            TextBlock typeLabel = new TextBlock
            {
                Text = "Типоразмер марки ADSK:",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            contentStack.Children.Add(typeLabel);

            _typeComboBox = new WpfComboBox
            {
                FontSize = 12.5,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 12)
            };
            foreach (string t in RoomTagManager.StandardAdskTypes)
            {
                _typeComboBox.Items.Add(t);
            }
            _typeComboBox.SelectedIndex = 0; // Номер Имя
            contentStack.Children.Add(_typeComboBox);

            TextBlock scopeLabel = new TextBlock
            {
                Text = "Область применения:",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            contentStack.Children.Add(scopeLabel);

            _allViewsRadio = new RadioButton
            {
                Content = "Во всем проекте (все планы этажей)",
                IsChecked = true,
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 4)
            };
            contentStack.Children.Add(_allViewsRadio);

            _activeViewRadio = new RadioButton
            {
                Content = $"Только на текущем виде («{activeViewName}»)",
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 12)
            };
            contentStack.Children.Add(_activeViewRadio);

            _tagUntaggedCheckBox = new CheckBox
            {
                Content = "Маркировать помещения, где марки еще не стоят",
                IsChecked = true,
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 8)
            };
            contentStack.Children.Add(_tagUntaggedCheckBox);

            _renumberCheckBox = new CheckBox
            {
                Content = "🔢 Упорядочить нумерацию помещений по ГОСТ (1, 2, 3...)",
                IsChecked = true,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(4, 0, 0, 10)
            };
            contentStack.Children.Add(_renumberCheckBox);

            WpfGrid.SetRow(contentStack, 0);
            rootGrid.Children.Add(contentStack);

            // Кнопки
            StackPanel btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            Button cancelBtn = new Button
            {
                Content = "Отмена",
                Width = 95,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225))
            };
            cancelBtn.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };

            Button okBtn = new Button
            {
                Content = "Заменить марки на ADSK",
                Width = 190,
                Height = 32,
                IsDefault = true,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(2, 132, 199)),
                BorderThickness = new Thickness(0)
            };
            okBtn.Click += (_, _) =>
            {
                DialogResult = true;
                Close();
            };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);

            WpfGrid.SetRow(btnPanel, 1);
            rootGrid.Children.Add(btnPanel);

            rootBorder.Child = rootGrid;
            Content = rootBorder;
        }
    }
}
