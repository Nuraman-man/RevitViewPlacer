using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using RevitColor = Autodesk.Revit.DB.Color;
using WpfGrid = System.Windows.Controls.Grid;
using WpfLine = System.Windows.Shapes.Line;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfPolygon = System.Windows.Shapes.Polygon;
using WpfDock = System.Windows.Controls.Dock;

namespace RevitViewPlacer
{
    public sealed record Point2D(double X, double Y);

    public sealed class SiteZonePolygon
    {
        public string Name { get; set; } = "Зона";
        public string CategoryKey { get; set; } = "ASPHALT"; // ASPHALT, PAVING, GREEN
        public List<Point2D> Points { get; set; } = [];
        public double InnerHoleAreaM2 { get; set; } = 0.0;
        public bool IsDefaultPerimeterRing { get; set; } = false;

        public double CalculateAreaM2()
        {
            if (Points == null || Points.Count < 3) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < Points.Count; i++)
            {
                Point2D p0 = Points[i];
                Point2D p1 = Points[(i + 1) % Points.Count];
                sum += (p0.X * p1.Y) - (p1.X * p0.Y);
            }
            return Math.Max(0.0, (Math.Abs(sum) * 0.5) - InnerHoleAreaM2);
        }
    }

    internal sealed class EditorHistoryState
    {
        public List<Point2D> BoundaryPoints { get; init; } = [];
        public List<SiteZonePolygon> AsphaltZones { get; init; } = [];
        public List<SiteZonePolygon> PavingZones { get; init; } = [];

        // Состояния функциональных зон генплана
        public bool IncludeTerrace { get; init; } = true;
        public bool IncludeFountainPlaza { get; init; } = true;
        public bool IncludeGuestParking { get; init; } = true;
        public bool IncludeServiceDock { get; init; } = true;
        public bool IncludeWasteSite { get; init; } = true;
        public bool IncludePlayground { get; init; } = true;
        public bool IncludeUtilityBlock { get; init; } = true;
        public bool IncludeStaffParking { get; init; } = true;
        public bool IncludePerimeterFence { get; init; } = true;
        public bool IncludePerimeterTrees { get; init; } = true;

        public static EditorHistoryState CreateSnapshot(
            List<Point2D> boundary,
            List<SiteZonePolygon> asphalt,
            List<SiteZonePolygon> paving,
            bool incTerrace = true,
            bool incPlaza = true,
            bool incGuest = true,
            bool incDock = true,
            bool incWaste = true,
            bool incKids = true,
            bool incUtility = true,
            bool incStaff = true,
            bool incFence = true,
            bool incTrees = true)
        {
            return new EditorHistoryState
            {
                BoundaryPoints = boundary.Select(p => new Point2D(p.X, p.Y)).ToList(),
                AsphaltZones = asphalt.Select(CloneZone).ToList(),
                PavingZones = paving.Select(CloneZone).ToList(),
                IncludeTerrace = incTerrace,
                IncludeFountainPlaza = incPlaza,
                IncludeGuestParking = incGuest,
                IncludeServiceDock = incDock,
                IncludeWasteSite = incWaste,
                IncludePlayground = incKids,
                IncludeUtilityBlock = incUtility,
                IncludeStaffParking = incStaff,
                IncludePerimeterFence = incFence,
                IncludePerimeterTrees = incTrees
            };
        }

        public static SiteZonePolygon CloneZone(SiteZonePolygon z)
        {
            return new SiteZonePolygon
            {
                Name = z.Name,
                CategoryKey = z.CategoryKey,
                Points = z.Points.Select(p => new Point2D(p.X, p.Y)).ToList(),
                InnerHoleAreaM2 = z.InnerHoleAreaM2,
                IsDefaultPerimeterRing = z.IsDefaultPerimeterRing
            };
        }
    }

    public sealed class SitePlanEditorConfig
    {
        // Кадастровая граница участка (относительно центра здания в метрах)
        public List<Point2D> BoundaryPoints { get; set; } = [];

        // Асфальтированные зоны (проезды, стоянки, дороги)
        public List<SiteZonePolygon> AsphaltZones { get; set; } = [];

        // Тротуарные зоны (пешеходные зоны, отмостка, брусчатка)
        public List<SiteZonePolygon> PavingZones { get; set; } = [];

        // Флаги наличия периметральной отмостки и кольцевого проезда вокруг здания
        public bool HasPerimeterBlindArea { get; set; } = true;
        public bool HasPerimeterRoad { get; set; } = true;

        // Функциональные зоны генплана (ГОСТ / СН РК): включение / отключение в редакторе
        public bool IncludeTerrace { get; set; } = true;          // ☕ Летняя веранда / открытая терраса
        public bool IncludeFountainPlaza { get; set; } = true;    // ⛲ Входной сквер с фонтаном и скамейками
        public bool IncludeGuestParking { get; set; } = true;     // 🅿️ Гостевая автостоянка
        public bool IncludeServiceDock { get; set; } = true;      // 🚚 Хоздвор и дебаркадер кухни
        public bool IncludeWasteSite { get; set; } = true;        // 🗑️ Контейнерная площадка ТБО
        public bool IncludePlayground { get; set; } = true;       // 🎠 Детский игровой уголок
        public bool IncludeUtilityBlock { get; set; } = true;     // ⚡ Инженерный блок (ТП / хладоцентр)
        public bool IncludeStaffParking { get; set; } = true;     // 🚙 Служебная автостоянка персонала
        public bool IncludePerimeterFence { get; set; } = true;   // 🧱 Ограждение территории (забор)
        public bool IncludePerimeterTrees { get; set; } = true;   // 🌳 Озеленение и деревья по периметру

        // Цвета слоев
        public RevitColor BuildingColor { get; set; } = new RevitColor(255, 255, 255);
        public RevitColor AsphaltColor { get; set; } = new RevitColor(60, 64, 70);
        public RevitColor PavingColor { get; set; } = new RevitColor(228, 222, 210);
        public RevitColor GreenColor { get; set; } = new RevitColor(152, 210, 136);
        public RevitColor CurbColor { get; set; } = new RevitColor(185, 190, 195);

        // Названия выбранных текстур каждого слоя
        public string AsphaltTextureName { get; set; } = "Тёмно-серый асфальт (ГОСТ)";
        public string PavingTextureName { get; set; } = "Тротуарная плитка (бежевая)";
        public string GreenTextureName { get; set; } = "Изумрудный газон";
        public string CurbTextureName { get; set; } = "Бетонный бордюр (ГОСТ)";

        // Опции отображения
        public bool WhiteBuildingFootprintOnly { get; set; } = true;
        public bool HideInternalWallsAndRooms { get; set; } = true;
        public bool CreateZoneExplicationTable { get; set; } = true;

        // Расчетные площади в м²
        public double BuildingAreaM2 { get; set; }
        public double AsphaltAreaM2 { get; set; }
        public double PavingAreaM2 { get; set; }
        public double GreenAreaM2 { get; set; }
        public double TotalSiteAreaM2 { get; set; }
    }

    internal enum EditorTool
    {
        Select,
        DrawBoundary,
        DrawAsphalt,
        DrawPaving
    }

    /// <summary>
    /// Интерактивное диалоговое окно подробного редактора генерального плана (ГП-1).
    /// Позволяет вручную отрисовывать границы участка и асфальтированные зоны в окошке,
    /// перетаскивать вершины контура, редактировать текстуры каждого слоя,
    /// рассчитывать экспликацию зон в реальном времени и настраивать белый контур здания сверху без стен и помещений.
    /// </summary>
    internal sealed class SitePlanEditorWindow : Window
    {
        private readonly List<Point2D> _buildingFootprintM;
        private readonly double _buildingWidthM;
        private readonly double _buildingLengthM;
        private readonly double _buildingAreaM2;
        private readonly GostStampData _initialStamp;

        private List<Point2D> _boundaryPoints = [];
        private readonly List<SiteZonePolygon> _asphaltZones = [];
        private readonly List<SiteZonePolygon> _pavingZones = [];

        // Стек истории изменений для Ctrl+Z и Ctrl+Y
        private readonly Stack<EditorHistoryState> _undoStack = new();
        private readonly Stack<EditorHistoryState> _redoStack = new();
        private EditorHistoryState? _dragStartSnapshot;
        private Point2D? _dragStartPoint;

        private EditorTool _currentTool = EditorTool.Select;
        private readonly List<Point2D> _currentDrawingPoints = [];
        private Point2D? _currentMouseMeters;

        // Перетаскивание вершин в режиме навигации
        private int _draggingBoundaryVertexIndex = -1;
        private SiteZonePolygon? _draggingZone;
        private int _draggingZoneVertexIndex = -1;

        // Canvas преобразование координат
        private double _zoomPxPerM = 6.0; // пикселей на метр
        private double _panX = 0.0;       // смещение центра в метрах
        private double _panY = 0.0;
        private System.Windows.Point? _lastDragPoint;

        // WPF элементы управления
        private readonly Canvas _canvas;
        private readonly TextBlock _statusCoordsText;
        private readonly TextBlock _toolPromptText;
        private readonly StackPanel _zonesListPanel;

        // Текстовые блоки экспликации
        private readonly TextBlock _txtBuildingArea;
        private readonly TextBlock _txtAsphaltArea;
        private readonly TextBlock _txtPavingArea;
        private readonly TextBlock _txtGreenArea;
        private readonly TextBlock _txtTotalArea;
        private readonly TextBlock _txtKz;
        private readonly TextBlock _txtKoz;

        // Выпадающие списки текстур каждого слоя и образцы текстур
        private readonly ComboBox _cbAsphaltTexture;
        private readonly ComboBox _cbPavingTexture;
        private readonly ComboBox _cbGreenTexture;
        private readonly ComboBox _cbCurbTexture;
        private readonly Border _swatchAsphalt;
        private readonly Border _swatchPaving;
        private readonly Border _swatchGreen;
        private readonly Border _swatchCurb;
        private readonly CheckBox _chkWhiteBuilding;
        private readonly CheckBox _chkCreateExplication;

        // Чекбоксы функциональных зон генплана (ГОСТ / СН РК)
        private bool _isRestoringState = false;
        private readonly CheckBox _chkTerrace;
        private readonly CheckBox _chkFountainPlaza;
        private readonly CheckBox _chkGuestParking;
        private readonly CheckBox _chkServiceDock;
        private readonly CheckBox _chkWasteSite;
        private readonly CheckBox _chkPlayground;
        private readonly CheckBox _chkUtilityBlock;
        private readonly CheckBox _chkStaffParking;
        private readonly CheckBox _chkPerimeterFence;
        private readonly CheckBox _chkPerimeterTrees;

        // Поля штампа ГОСТ РК (встроенные для быстрого доступа в одном окне)
        private readonly TextBox _tbDeveloper;
        private readonly TextBox _tbChecker;
        private readonly TextBox _tbGip;
        private readonly TextBox _tbProjectName;
        private readonly TextBox _tbProjectCode;
        private readonly TextBox _tbOrgName;
        private readonly TextBox _tbAddress;
        private readonly TextBox _tbGroup;
        private readonly ComboBox _cbPurpose;
        private readonly ComboBox _cbScale;

        // Кнопки инструментов
        private readonly Button _btnToolSelect;
        private readonly Button _btnToolBoundary;
        private readonly Button _btnToolAsphalt;
        private readonly Button _btnToolPaving;
        private readonly Button _btnUndo;
        private readonly Button _btnRedo;

        public SitePlanEditorConfig? ResultConfig { get; private set; }
        public GostStampData? UpdatedGostStamp { get; private set; }

        public SitePlanEditorWindow(CurveLoop buildingLoop, BoundingBoxXYZ buildingBox, GostStampData? gostStamp = null)
        {
            _initialStamp = gostStamp ?? new GostStampData(
                RawUserInput: "Турсынов И.Б.",
                FormattedDeveloperName: "Турсынов И.Б.",
                FormattedCheckerName: "Старков А.А.",
                FormattedNormControllerName: "Старков А.А.",
                FormattedApproverName: "Турсынов И.Б.",
                OrganizationName: "ТОО «Казгорпроект»",
                ProjectName: "Общественное здание",
                ProjectCode: "2026-АР",
                IssueDateGost: DateTime.Now.ToString("MM.yy", CultureInfo.InvariantCulture));

            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double screenWidth = SystemParameters.PrimaryScreenWidth;

            Title = "Подробный редактор ген-плана (ГП-1) — Отрисовка контура, асфальта, текстур слоев и экспликация зон";
            Width = Math.Min(1180, screenWidth * 0.94);
            Height = Math.Min(780, screenHeight * 0.90);
            MinWidth = 900;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = new WpfSolidColorBrush(WpfColor.FromRgb(248, 250, 252));

            // Анализируем геометрию здания
            XYZ center = (buildingBox.Min + buildingBox.Max) / 2.0;
            _buildingWidthM = UnitUtils.ConvertFromInternalUnits(buildingBox.Max.X - buildingBox.Min.X, UnitTypeId.Meters);
            _buildingLengthM = UnitUtils.ConvertFromInternalUnits(buildingBox.Max.Y - buildingBox.Min.Y, UnitTypeId.Meters);

            _buildingFootprintM = [];
            foreach (Curve curve in buildingLoop)
            {
                XYZ p = curve.GetEndPoint(0);
                double xM = UnitUtils.ConvertFromInternalUnits(p.X - center.X, UnitTypeId.Meters);
                double yM = UnitUtils.ConvertFromInternalUnits(p.Y - center.Y, UnitTypeId.Meters);
                _buildingFootprintM.Add(new Point2D(xM, yM));
            }

            _buildingAreaM2 = CalculatePolygonArea(_buildingFootprintM);
            if (_buildingAreaM2 <= 1.0)
            {
                _buildingAreaM2 = _buildingWidthM * _buildingLengthM;
            }

            // Создаем начальные типовые зоны по умолчанию (вокруг здания)
            InitDefaultZones();

            // Автоматически рассчитываем начальный масштаб зума под габариты здания и участка
            double maxDim = Math.Max(_buildingWidthM, _buildingLengthM);
            _zoomPxPerM = Math.Clamp(500.0 / (maxDim * 3.0), 3.0, 15.0);

            // Инициализация чекбоксов функциональных зон генплана
            _chkTerrace = CreateZoneCheckBox("☕ Летняя веранда / открытая терраса (со столиками)", true, "Включить/выключить открытую террасу ресторана (№ 2)");
            _chkFountainPlaza = CreateZoneCheckBox("⛲ Входной сквер с фонтаном и скамейками", true, "Включить/выключить парадный входной сквер (№ 3)");
            _chkGuestParking = CreateZoneCheckBox("🅿️ Гостевая автостоянка для посетителей", true, "Включить/выключить открытую автостоянку (№ 4)");
            _chkServiceDock = CreateZoneCheckBox("🚚 Хоздвор и дебаркадер разгрузки кухни", true, "Включить/выключить хоздвор кухни (№ 5)");
            _chkWasteSite = CreateZoneCheckBox("🗑️ Контейнерная площадка ТБО", true, "Включить/выключить площадку мусоросборников ТБО (№ 6)");
            _chkPlayground = CreateZoneCheckBox("🎠 Детский семейный игровой уголок", true, "Включить/выключить игровую зону (№ 7)");
            _chkUtilityBlock = CreateZoneCheckBox("⚡ Инженерный блок (ТП / хладоцентр)", true, "Включить/выключить блок ТП и хладоцентра (№ 8)");
            _chkStaffParking = CreateZoneCheckBox("🚙 Служебная автостоянка персонала", true, "Включить/выключить служебную парковку (№ 9)");
            _chkPerimeterFence = CreateZoneCheckBox("🧱 Ограждение территории (забор 1.8 м)", true, "Включить/выключить 3D-ограждение по границе");
            _chkPerimeterTrees = CreateZoneCheckBox("🌳 Озеленение и деревья по периметру", true, "Включить/выключить 3D-деревья по периметру");

            // =====================================================================
            // ВЕРСТКА ОКНА (Grid: Левая часть — Холст в окошке, Правая часть — Настройки)
            // =====================================================================
            WpfGrid rootGrid = new WpfGrid();
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(415, GridUnitType.Pixel) });

            // ---------------------------------------------------------------------
            // ЛЕВАЯ КОЛОНКА: Интерактивный холст отрисовки + Тулбар + Статус-бар
            // ---------------------------------------------------------------------
            WpfGrid canvasContainerGrid = new WpfGrid();
            canvasContainerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Тулбар инструментов
            canvasContainerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Холст
            canvasContainerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Статус-бар

            // Панель инструментов над холстом
            WrapPanel toolbar = new WrapPanel
            {
                Margin = new Thickness(10, 10, 10, 6)
            };

            _btnToolSelect = CreateToolButton("🖱️ Двигать вершины / Панорама", EditorTool.Select);
            _btnToolBoundary = CreateToolButton("🟥 Нарисовать контур участка", EditorTool.DrawBoundary);
            _btnToolAsphalt = CreateToolButton("⬛ Отрисовать асфальтированную зону", EditorTool.DrawAsphalt);
            _btnToolPaving = CreateToolButton("🟨 Отрисовать тротуар / плитку", EditorTool.DrawPaving);

            toolbar.Children.Add(_btnToolSelect);
            toolbar.Children.Add(_btnToolBoundary);
            toolbar.Children.Add(_btnToolAsphalt);
            toolbar.Children.Add(_btnToolPaving);

            // Кнопки быстрой отмены и повтора (Ctrl+Z / Ctrl+Y)
            _btnUndo = CreateSmallActionButton("↩️ Отменить (Ctrl+Z)", (_, _) => Undo());
            _btnRedo = CreateSmallActionButton("↪️ Повторить (Ctrl+Y)", (_, _) => Redo());
            _btnUndo.ToolTip = "Отменить последнее действие или удаленную точку контура (Ctrl+Z)";
            _btnRedo.ToolTip = "Повторить ранее отмененное действие (Ctrl+Y)";
            toolbar.Children.Add(_btnUndo);
            toolbar.Children.Add(_btnRedo);

            // Быстрые шаблоны
            Button btnToggleTerrace = CreateSmallActionButton("☕ Веранда: ВКЛ/ВЫКЛ", (_, _) =>
            {
                _chkTerrace.IsChecked = !(_chkTerrace.IsChecked == true);
            });
            btnToggleTerrace.ToolTip = "Быстро переключить наличие открытой летней веранды (№ 2)";
            toolbar.Children.Add(btnToggleTerrace);

            Button btnAddDriveway = CreateSmallActionButton("➕ Круговой проезд (6 м)", (_, _) => { PushUndoState(); AddDefaultCircularDriveway(); });
            Button btnAddParking = CreateSmallActionButton("🅿️ Парковка спереди", (_, _) => { PushUndoState(); AddFrontParkingZone(); });
            Button btnRectBoundary = CreateSmallActionButton("📐 Прямоугольный контур", (_, _) => { PushUndoState(); SetRectangularBoundary(); });
            Button btnResetZones = CreateSmallActionButton("🔄 Сброс к стандарту", (_, _) => { PushUndoState(); InitDefaultZones(); RedrawCanvas(); });
            Button btnClearZones = CreateSmallActionButton("🗑️ Очистить асфальт/плитку", (_, _) => { PushUndoState(); _asphaltZones.Clear(); _pavingZones.Clear(); RedrawCanvas(); });

            toolbar.Children.Add(btnAddDriveway);
            toolbar.Children.Add(btnAddParking);
            toolbar.Children.Add(btnRectBoundary);
            toolbar.Children.Add(btnResetZones);
            toolbar.Children.Add(btnClearZones);

            canvasContainerGrid.Children.Add(toolbar);
            WpfGrid.SetRow(toolbar, 0);

            // Интерактивный холст в рамке
            Border canvasBorder = new Border
            {
                Margin = new Thickness(10, 0, 10, 6),
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(241, 245, 249)),
                ClipToBounds = true
            };

            _canvas = new Canvas
            {
                Background = Brushes.Transparent,
                Focusable = true,
                ClipToBounds = true,
                Cursor = Cursors.Cross
            };

            _canvas.MouseDown += Canvas_MouseDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseUp += Canvas_MouseUp;
            _canvas.MouseWheel += Canvas_MouseWheel;
            _canvas.SizeChanged += (_, _) => RedrawCanvas();

            canvasBorder.Child = _canvas;
            canvasContainerGrid.Children.Add(canvasBorder);
            WpfGrid.SetRow(canvasBorder, 1);

            // Нижний статус-бар под холстом
            Border statusBar = new Border
            {
                Margin = new Thickness(10, 0, 10, 10),
                Padding = new Thickness(10, 6, 10, 6),
                Background = Brushes.White,
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5)
            };

            DockPanel statusDock = new DockPanel();
            _statusCoordsText = new TextBlock
            {
                Text = "X: 0.0 м, Y: 0.0 м | Зум: 100%",
                FontWeight = FontWeights.SemiBold,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(51, 65, 85)),
                FontSize = 11.5
            };
            DockPanel.SetDock(_statusCoordsText, WpfDock.Right);
            statusDock.Children.Add(_statusCoordsText);

            _toolPromptText = new TextBlock
            {
                Text = "Кликайте на холсте для отрисовки или перетаскивайте красные вершины контура мышью.",
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(37, 99, 235)),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            statusDock.Children.Add(_toolPromptText);

            statusBar.Child = statusDock;
            canvasContainerGrid.Children.Add(statusBar);
            WpfGrid.SetRow(statusBar, 2);

            rootGrid.Children.Add(canvasContainerGrid);
            WpfGrid.SetColumn(canvasContainerGrid, 0);

            // ---------------------------------------------------------------------
            // ПРАВАЯ КОЛОНКА: Прокручиваемая панель слоев, текстур, экспликации и кнопки
            // ---------------------------------------------------------------------
            Border sideBorder = new Border
            {
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Background = Brushes.White,
                Padding = new Thickness(14, 12, 14, 12)
            };

            WpfGrid sideRightGrid = new WpfGrid();
            sideRightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            sideRightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            ScrollViewer sideScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0)
            };
            sideScroll.PreviewMouseWheel += (sender, e) =>
            {
                if (e.OriginalSource is DependencyObject d)
                {
                    DependencyObject? parent = d;
                    bool insideOpenPopup = false;
                    while (parent != null)
                    {
                        if (parent is System.Windows.Controls.Primitives.Popup)
                        {
                            insideOpenPopup = true;
                            break;
                        }
                        parent = VisualTreeHelper.GetParent(parent);
                    }
                    if (!insideOpenPopup && sender is ScrollViewer sv)
                    {
                        sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta * 0.7));
                        e.Handled = true;
                    }
                }
            };

            StackPanel sideStack = new StackPanel();

            // 1. Заголовок боковой панели
            TextBlock sideHeader = new TextBlock
            {
                Text = "Редактор слоев, текстур и экспликации",
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(15, 23, 42)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            sideStack.Children.Add(sideHeader);

            // Карточка отображения здания сверху (белый контур без стен и помещений)
            Border bldgInfoCard = new Border
            {
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(248, 250, 252)),
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            StackPanel bldgInfoStack = new StackPanel();
            bldgInfoStack.Children.Add(new TextBlock
            {
                Text = $"🏢 Слой 1 — Здание сверху: {_buildingWidthM:F1} × {_buildingLengthM:F1} м (S = {_buildingAreaM2:F1} м²)",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(15, 23, 42))
            });

            _chkWhiteBuilding = new CheckBox
            {
                Content = "Чистый белый контур здания сверху (скрыть стены и помещения)",
                IsChecked = true,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.2,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(3, 105, 161)),
                Margin = new Thickness(0, 5, 0, 0)
            };
            _chkWhiteBuilding.Checked += (_, _) => RedrawCanvas();
            _chkWhiteBuilding.Unchecked += (_, _) => RedrawCanvas();
            bldgInfoStack.Children.Add(_chkWhiteBuilding);

            bldgInfoCard.Child = bldgInfoStack;
            sideStack.Children.Add(bldgInfoCard);

            // 2. Блок выбора текстур и цветов каждого слоя
            sideStack.Children.Add(CreateSectionHeader("Текстура каждого слоя (3D + 2D на листе)"));

            _swatchAsphalt = CreateTextureSwatch(GetSelectedAsphaltBrush());
            _cbAsphaltTexture = CreateStyledComboBox(
            [
                "Тёмно-серый асфальт (ГОСТ #3C4043)",
                "Классический битумный асфальт (#5A5F69)",
                "Светло-серый асфальтобетон (#787F8B)",
                "Графитовый асфальт крупнозернистый (#282A2E)",
                "Асфальт с диагональной штриховкой ГОСТ (#464B53)"
            ], 0);
            _cbAsphaltTexture.SelectionChanged += (_, _) =>
            {
                _swatchAsphalt.Background = GetSelectedAsphaltBrush();
                RedrawCanvas();
            };
            sideStack.Children.Add(CreateLayerTextureRow("⬛ Слой 2 — Асфальт (дороги, стоянки):", _cbAsphaltTexture, _swatchAsphalt));

            _swatchPaving = CreateTextureSwatch(GetSelectedPavingBrush());
            _cbPavingTexture = CreateStyledComboBox(
            [
                "Тротуарная плитка бежевая (#E4DED2)",
                "Серая гранитная брусчатка (#C5BBA8)",
                "Терракотовая клинкерная плитка (#C07D58)",
                "Светлый полированный гранит (#ECEFF1)",
                "Песчаник золотистый (#E6CCA0)"
            ], 0);
            _cbPavingTexture.SelectionChanged += (_, _) =>
            {
                _swatchPaving.Background = GetSelectedPavingBrush();
                RedrawCanvas();
            };
            sideStack.Children.Add(CreateLayerTextureRow("🟨 Слой 3 — Тротуары и отмостка:", _cbPavingTexture, _swatchPaving));

            _swatchGreen = CreateTextureSwatch(GetSelectedGreenBrush());
            _cbGreenTexture = CreateStyledComboBox(
            [
                "Изумрудный газон (#98D288)",
                "Свежая луговая трава (#A8E098)",
                "Тёмный парковый газон (#5E9E50)",
                "Оливковый ландшафтный газон (#8FBC6B)"
            ], 0);
            _cbGreenTexture.SelectionChanged += (_, _) =>
            {
                _swatchGreen.Background = GetSelectedGreenBrush();
                RedrawCanvas();
            };
            sideStack.Children.Add(CreateLayerTextureRow("🟩 Слой 4 — Газон и озеленение:", _cbGreenTexture, _swatchGreen));

            _swatchCurb = CreateTextureSwatch(GetSelectedCurbBrush());
            _cbCurbTexture = CreateStyledComboBox(
            [
                "Бетонный бордюр БР 100.30.15 (#B9BEC3)",
                "Гранитный бордюр тёмно-серый (#8C9298)",
                "Белый декоративный поребрик (#E2E8F0)"
            ], 0);
            _cbCurbTexture.SelectionChanged += (_, _) =>
            {
                _swatchCurb.Background = GetSelectedCurbBrush();
                RedrawCanvas();
            };
            sideStack.Children.Add(CreateLayerTextureRow("⬜ Слой 5 — Бордюрный камень:", _cbCurbTexture, _swatchCurb));

            // 3. Блок экспликации зон и баланса территории (ГОСТ 21.508-2020)
            sideStack.Children.Add(CreateSectionHeader("Экспликация зон на листе ГП-1 (ГОСТ 21.508)"));

            Border explicationCard = new Border
            {
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(241, 245, 249)),
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 2, 0, 6)
            };

            StackPanel expStack = new StackPanel();

            _txtBuildingArea = CreateAreaRow(expStack, "1. Зона застройки (Здание):", Brushes.White, "#000000");
            _txtAsphaltArea = CreateAreaRow(expStack, "2. Асфальтированные зоны:", new WpfSolidColorBrush(WpfColor.FromRgb(60, 64, 70)), "#0F172A");
            _txtPavingArea = CreateAreaRow(expStack, "3. Тротуары и отмостка:", new WpfSolidColorBrush(WpfColor.FromRgb(228, 222, 210)), "#0F172A");
            _txtGreenArea = CreateAreaRow(expStack, "4. Озеленение (газон):", new WpfSolidColorBrush(WpfColor.FromRgb(152, 210, 136)), "#0F172A");

            Separator sep = new Separator { Margin = new Thickness(0, 5, 0, 5), Background = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225)) };
            expStack.Children.Add(sep);

            _txtTotalArea = CreateAreaRow(expStack, "Итого площадь участка:", Brushes.Transparent, "#0F172A", isBold: true);
            _txtKz = CreateAreaRow(expStack, "Коэффициент застройки (Кз):", Brushes.Transparent, "#334155");
            _txtKoz = CreateAreaRow(expStack, "Коэффициент озеленения (Коз):", Brushes.Transparent, "#334155");

            explicationCard.Child = expStack;
            sideStack.Children.Add(explicationCard);

            _chkCreateExplication = new CheckBox
            {
                Content = "Начертить таблицу «Экспликация зон и баланс территории» на листе ГП-1",
                IsChecked = true,
                FontSize = 11.2,
                FontWeight = FontWeights.SemiBold,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(15, 23, 42)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            sideStack.Children.Add(_chkCreateExplication);

            // 4. Функциональные зоны генплана (ГОСТ / СН РК)
            sideStack.Children.Add(CreateSectionHeader("Функциональные зоны генплана (ГОСТ / СН РК)"));

            Border funcZonesCard = new Border
            {
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(248, 250, 252)),
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 2, 0, 8)
            };

            StackPanel funcZonesStack = new StackPanel();
            funcZonesStack.Children.Add(new TextBlock
            {
                Text = "Включайте или отключайте любые зоны (экспликация, нумерация и 3D-модель перестроятся автоматически):",
                FontSize = 10.5,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(100, 116, 139)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            funcZonesStack.Children.Add(_chkTerrace);
            funcZonesStack.Children.Add(_chkFountainPlaza);
            funcZonesStack.Children.Add(_chkGuestParking);
            funcZonesStack.Children.Add(_chkServiceDock);
            funcZonesStack.Children.Add(_chkWasteSite);
            funcZonesStack.Children.Add(_chkPlayground);
            funcZonesStack.Children.Add(_chkUtilityBlock);
            funcZonesStack.Children.Add(_chkStaffParking);
            funcZonesStack.Children.Add(_chkPerimeterFence);
            funcZonesStack.Children.Add(_chkPerimeterTrees);

            WrapPanel quickZoneBtns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            Button btnQuickTerrace = CreateSmallActionButton("☕ Убрать/вернуть террасу", (_, _) =>
            {
                _chkTerrace.IsChecked = !(_chkTerrace.IsChecked == true);
            });
            btnQuickTerrace.ToolTip = "Быстро переключить наличие открытой летней веранды (№ 2)";

            Button btnSelectAll = CreateSmallActionButton("✅ Все зоны", (_, _) =>
            {
                PushUndoState();
                SetAllFunctionalZones(true);
            });
            Button btnSelectNone = CreateSmallActionButton("❌ Без доп. зон", (_, _) =>
            {
                PushUndoState();
                SetAllFunctionalZones(false);
            });

            quickZoneBtns.Children.Add(btnQuickTerrace);
            quickZoneBtns.Children.Add(btnSelectAll);
            quickZoneBtns.Children.Add(btnSelectNone);
            funcZonesStack.Children.Add(quickZoneBtns);

            funcZonesCard.Child = funcZonesStack;
            sideStack.Children.Add(funcZonesCard);

            // 5. Список отрисованных зон с возможностью удаления любой зоны
            sideStack.Children.Add(CreateSectionHeader("Список зон участка (можно удалять / добавлять)"));
            _zonesListPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            sideStack.Children.Add(_zonesListPanel);

            // 6. Реквизиты штампа по ГОСТ РК и масштаб ГП-1 (в этом же окне с прокруткой)
            sideStack.Children.Add(CreateSectionHeader("Реквизиты штампа ГОСТ РК и параметры ГП-1"));

            Border stampCard = new Border
            {
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(248, 250, 252)),
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            StackPanel stampStack = new StackPanel();

            _tbDeveloper = CreateTextBox(_initialStamp.FormattedDeveloperName);
            _tbChecker = CreateTextBox(_initialStamp.FormattedCheckerName);
            _tbGip = CreateTextBox(_initialStamp.FormattedApproverName);
            _tbProjectName = CreateTextBox(_initialStamp.ProjectName);
            _tbProjectCode = CreateTextBox(_initialStamp.ProjectCode);
            _tbOrgName = CreateTextBox(_initialStamp.OrganizationName);
            _tbAddress = CreateTextBox(string.IsNullOrWhiteSpace(_initialStamp.ProjectAddress) ? "г. Алматы, ул. Сатпаева, 22" : _initialStamp.ProjectAddress);
            _tbGroup = CreateTextBox(_initialStamp.StudentGroup ?? string.Empty);

            stampStack.Children.Add(CreateFieldRow("Разработал (ФИО):", _tbDeveloper));
            stampStack.Children.Add(CreateFieldRow("Проверил (ФИО):", _tbChecker));
            stampStack.Children.Add(CreateFieldRow("ГИП (ФИО):", _tbGip));
            stampStack.Children.Add(CreateFieldRow("Наименование объекта:", _tbProjectName));
            stampStack.Children.Add(CreateFieldRow("Шифр проекта:", _tbProjectCode));
            stampStack.Children.Add(CreateFieldRow("Проектная орг. РК:", _tbOrgName));
            stampStack.Children.Add(CreateFieldRow("Адрес объекта (Сит. схема):", _tbAddress));
            stampStack.Children.Add(CreateFieldRow("Группа (для обложки):", _tbGroup));

            _cbPurpose = CreateStyledComboBox(
            [
                "Ресторан / Кафе / Общепит (веранда, дебаркадер, фонтан)",
                "Жилой многоквартирный дом (детская, спорт, ТБО)",
                "Торговый центр / Супермаркет (парковка, ТБО)",
                "Бизнес-центр / Офисное здание (аллея, паркинг)",
                "Гостиница / Отель (курортная зона, трансфер)",
                "Детский сад / Школа (спортядро, игровые)",
                "Складской комплекс / Производство (фуры, КПП)"
            ], (int)_initialStamp.PurposeCategory);
            stampStack.Children.Add(CreateFieldRow("Назначение объекта:", _cbPurpose));

            _cbScale = CreateStyledComboBox(["М 1:500", "М 1:200", "М 1:250", "М 1:300", "М 1:400", "М 1:1000"], 0);
            stampStack.Children.Add(CreateFieldRow("Масштаб плана ГП-1:", _cbScale));

            stampCard.Child = stampStack;
            sideStack.Children.Add(stampCard);

            sideScroll.Content = sideStack;
            WpfGrid.SetRow(sideScroll, 0);
            sideRightGrid.Children.Add(sideScroll);

            // 6. Закрепленная нижняя панель кнопок («Применить» и «Отмена»)
            Border bottomBtnBorder = new Border
            {
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 10, 0, 0),
                Margin = new Thickness(0, 6, 0, 0)
            };

            StackPanel btnStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button btnCancel = new Button
            {
                Content = "Отмена",
                Width = 90,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(241, 245, 249)),
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(51, 65, 85)),
                FontWeight = FontWeights.Medium,
                Cursor = Cursors.Hand
            };
            btnCancel.Click += (_, _) => { DialogResult = false; Close(); };

            Button btnApply = new Button
            {
                Content = "✅ Построить 3D ген-план и лист ГП-1",
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(16, 185, 129)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnApply.Click += BtnApply_Click;

            btnStack.Children.Add(btnCancel);
            btnStack.Children.Add(btnApply);
            bottomBtnBorder.Child = btnStack;

            WpfGrid.SetRow(bottomBtnBorder, 1);
            sideRightGrid.Children.Add(bottomBtnBorder);

            sideBorder.Child = sideRightGrid;
            rootGrid.Children.Add(sideBorder);
            WpfGrid.SetColumn(sideBorder, 1);

            Content = rootGrid;

            // Поддержка горячих клавиш (Ctrl+Z для отмены, Ctrl+Y для повтора, Backspace, Esc)
            PreviewKeyDown += SitePlanEditorWindow_PreviewKeyDown;
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Undo()));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Redo()));

            Loaded += (_, _) =>
            {
                Focus();
                UpdateToolSelectionUI();
                UpdateUndoRedoUI();
                RedrawCanvas();
            };
        }

        // =========================================================================
        // ИНИЦИАЛИЗАЦИЯ И РАСЧЕТ ТИПОВЫХ ЗОН
        // =========================================================================

        private void InitDefaultZones()
        {
            _boundaryPoints.Clear();
            _asphaltZones.Clear();
            _pavingZones.Clear();

            double halfW = _buildingWidthM / 2.0;
            double halfL = _buildingLengthM / 2.0;

            // 1. Граница участка (со скосом углов, отступ ~22 м от здания)
            double bPadX = Math.Max(22.0, halfW * 1.5);
            double bPadY = Math.Max(20.0, halfL * 1.4);

            double minX = -halfW - bPadX;
            double maxX = halfW + bPadX;
            double minY = -halfL - bPadY;
            double maxY = halfL + bPadY;

            _boundaryPoints =
            [
                new Point2D(minX, minY + 6.0),
                new Point2D(minX, maxY - 12.0),
                new Point2D(minX + 12.0, maxY),
                new Point2D(maxX - 8.0, maxY),
                new Point2D(maxX, maxY - 6.0),
                new Point2D(maxX, minY),
                new Point2D(minX + 6.0, minY)
            ];

            // 2. Отмостка и тротуары вокруг здания (+1.5 м, чистая площадь кольца без здания)
            AddOffsetBandZone("Отмостка и тротуар (плитка 1.5 м)", 0.0, 1.5, isPaving: true, isDefaultRing: true);

            // 3. Асфальтированный проезд вокруг здания (ширина 6 м, отступ 1.5 .. 7.5 м, чистая площадь кольца)
            AddOffsetBandZone("Кольцевой проезд (асфальт 6 м)", 1.5, 7.5, isPaving: false, isDefaultRing: true);

            // 4. Гостевая парковка и въезд
            AddFrontParkingZone();
        }

        private void SetRectangularBoundary()
        {
            double halfW = _buildingWidthM / 2.0;
            double halfL = _buildingLengthM / 2.0;
            double bPadX = Math.Max(22.0, halfW * 1.5);
            double bPadY = Math.Max(20.0, halfL * 1.4);

            _boundaryPoints =
            [
                new Point2D(-halfW - bPadX, -halfL - bPadY),
                new Point2D(-halfW - bPadX, halfL + bPadY),
                new Point2D(halfW + bPadX, halfL + bPadY),
                new Point2D(halfW + bPadX, -halfL - bPadY)
            ];
            RedrawCanvas();
        }

        private void AddOffsetBandZone(string name, double innerDistM, double outerDistM, bool isPaving, bool isDefaultRing)
        {
            double halfW = _buildingWidthM / 2.0;
            double halfL = _buildingLengthM / 2.0;

            List<Point2D> pts =
            [
                new Point2D(-halfW - outerDistM, -halfL - outerDistM),
                new Point2D(-halfW - outerDistM, halfL + outerDistM),
                new Point2D(halfW + outerDistM, halfL + outerDistM),
                new Point2D(halfW + outerDistM, -halfL - outerDistM)
            ];

            double innerArea = innerDistM <= 0.01
                ? _buildingAreaM2
                : (_buildingWidthM + (2.0 * innerDistM)) * (_buildingLengthM + (2.0 * innerDistM));

            SiteZonePolygon zone = new SiteZonePolygon
            {
                Name = name,
                CategoryKey = isPaving ? "PAVING" : "ASPHALT",
                Points = pts,
                InnerHoleAreaM2 = innerArea,
                IsDefaultPerimeterRing = isDefaultRing
            };

            if (isPaving) _pavingZones.Add(zone);
            else _asphaltZones.Add(zone);
        }

        private void AddDefaultCircularDriveway()
        {
            if (!_asphaltZones.Any(z => z.IsDefaultPerimeterRing))
            {
                AddOffsetBandZone("Кольцевой проезд (асфальт 6 м)", 1.5, 7.5, isPaving: false, isDefaultRing: true);
            }
            else
            {
                double halfW = _buildingWidthM / 2.0;
                double halfL = _buildingLengthM / 2.0;
                double yMin = _boundaryPoints.Count > 0 ? _boundaryPoints.Min(p => p.Y) : -halfL - 22.0;
                _asphaltZones.Add(new SiteZonePolygon
                {
                    Name = $"Въезд с улицы #{_asphaltZones.Count + 1} (асфальт)",
                    CategoryKey = "ASPHALT",
                    Points =
                    [
                        new Point2D(-3.0, yMin),
                        new Point2D(-3.0, -halfL - 7.5),
                        new Point2D(3.0, -halfL - 7.5),
                        new Point2D(3.0, yMin)
                    ]
                });
            }
            RedrawCanvas();
        }

        private void AddFrontParkingZone()
        {
            double halfW = _buildingWidthM / 2.0;
            double halfL = _buildingLengthM / 2.0;

            double pW = Math.Max(20.0, _buildingWidthM * 0.95);
            double pL = 5.5;
            double pY = -halfL - 7.5 - pL;

            List<Point2D> pts =
            [
                new Point2D(-pW / 2.0, pY),
                new Point2D(-pW / 2.0, pY + pL),
                new Point2D(pW / 2.0, pY + pL),
                new Point2D(pW / 2.0, pY)
            ];

            _asphaltZones.Add(new SiteZonePolygon
            {
                Name = $"Асфальтированная парковка #{_asphaltZones.Count + 1}",
                CategoryKey = "ASPHALT",
                Points = pts
            });
            RedrawCanvas();
        }

        // =========================================================================
        // ОТРИСОВКА ХОЛСТА (CANVAS)
        // =========================================================================

        private void RedrawCanvas()
        {
            if (_canvas == null || _canvas.ActualWidth < 10 || _canvas.ActualHeight < 10)
            {
                return;
            }

            _canvas.Children.Clear();

            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            double cx = w / 2.0;
            double cy = h / 2.0;

            // 1. Фоновая сетка координат (через каждые 5 м, утолщение каждые 20 м)
            DrawBackgroundGrid(w, h, cx, cy);

            // Текстурные кисти слоев из комбобоксов
            WpfBrush asphaltBrush = GetSelectedAsphaltBrush();
            WpfBrush pavingBrush = GetSelectedPavingBrush();
            WpfBrush greenBrush = GetSelectedGreenBrush();
            WpfBrush curbBrush = GetSelectedCurbBrush();

            // 2. Слой Озеленение (Газон): фон внутри границы участка
            if (_boundaryPoints.Count >= 3)
            {
                WpfPolygon greenPoly = new WpfPolygon
                {
                    Fill = greenBrush,
                    Stroke = Brushes.Transparent
                };
                foreach (Point2D p in _boundaryPoints)
                {
                    greenPoly.Points.Add(WorldToScreen(p, cx, cy));
                }
                _canvas.Children.Add(greenPoly);
            }

            // 3. Слой Асфальтированные зоны (проезды, стоянки, дороги)
            foreach (SiteZonePolygon zone in _asphaltZones)
            {
                if (zone.Points.Count < 3) continue;
                WpfPolygon poly = new WpfPolygon
                {
                    Fill = asphaltBrush,
                    Stroke = curbBrush,
                    StrokeThickness = 2.0
                };
                foreach (Point2D p in zone.Points)
                {
                    poly.Points.Add(WorldToScreen(p, cx, cy));
                }
                _canvas.Children.Add(poly);

                // Если это не кольцевая зона вокруг здания, подписываем в центре и рисуем узлы вершин
                if (!zone.IsDefaultPerimeterRing)
                {
                    Point2D centerPt = ComputePolygonCenter(zone.Points);
                    System.Windows.Point scPt = WorldToScreen(centerPt, cx, cy);
                    Border badge = new Border
                    {
                        Background = new WpfSolidColorBrush(WpfColor.FromArgb(190, 15, 23, 42)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        IsHitTestVisible = false
                    };
                    badge.Child = new TextBlock
                    {
                        Text = $"{zone.Name} ({zone.CalculateAreaM2():F0} м²)",
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White
                    };
                    Canvas.SetLeft(badge, scPt.X - 48);
                    Canvas.SetTop(badge, scPt.Y - 9);
                    _canvas.Children.Add(badge);

                    DrawEditableZoneVertices(zone, cx, cy, WpfColor.FromRgb(51, 65, 85));
                }
            }

            // 4. Слой Тротуары, мощение и отмостка
            foreach (SiteZonePolygon zone in _pavingZones)
            {
                if (zone.Points.Count < 3) continue;
                WpfPolygon poly = new WpfPolygon
                {
                    Fill = pavingBrush,
                    Stroke = curbBrush,
                    StrokeThickness = 1.6
                };
                foreach (Point2D p in zone.Points)
                {
                    poly.Points.Add(WorldToScreen(p, cx, cy));
                }
                _canvas.Children.Add(poly);

                if (!zone.IsDefaultPerimeterRing)
                {
                    DrawEditableZoneVertices(zone, cx, cy, WpfColor.FromRgb(180, 83, 9));
                }
            }

            // 4.1. Превью активных функциональных зон генплана (веранда, сквер, детская площадка и т.д.)
            DrawFunctionalZonesPreview(cx, cy);

            // 5. Слой Контур здания сверху: чисто белого цвета с четким черным контуром по ГОСТ (без стен и помещений)!
            if (_buildingFootprintM.Count >= 3)
            {
                WpfPolygon bldgPoly = new WpfPolygon
                {
                    Fill = Brushes.White,
                    Stroke = Brushes.Black,
                    StrokeThickness = 3.0
                };
                foreach (Point2D p in _buildingFootprintM)
                {
                    bldgPoly.Points.Add(WorldToScreen(p, cx, cy));
                }
                _canvas.Children.Add(bldgPoly);

                // Номер здания по ГОСТ 21.508 ("1") и габариты на белом контуре
                System.Windows.Point bCenterSc = WorldToScreen(new Point2D(0, 0), cx, cy);

                Border bldgTag = new Border
                {
                    Background = new WpfSolidColorBrush(WpfColor.FromArgb(235, 255, 255, 255)),
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(7, 2, 7, 2),
                    IsHitTestVisible = false
                };
                StackPanel bldgTagStack = new StackPanel();
                bldgTagStack.Children.Add(new TextBlock
                {
                    Text = "① ЗДАНИЕ (белый контур)",
                    FontWeight = FontWeights.Bold,
                    FontSize = 10.5,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                bldgTagStack.Children.Add(new TextBlock
                {
                    Text = $"{_buildingWidthM:F1}×{_buildingLengthM:F1} м ({_buildingAreaM2:F1} м²)",
                    FontSize = 9.2,
                    Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(51, 65, 85)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                bldgTag.Child = bldgTagStack;

                Canvas.SetLeft(bldgTag, bCenterSc.X - 68);
                Canvas.SetTop(bldgTag, bCenterSc.Y - 17);
                _canvas.Children.Add(bldgTag);
            }

            // 6. Красная граница участка (штрихпунктир с перетаскиваемыми точками-вершинами)
            if (_boundaryPoints.Count >= 3)
            {
                WpfPolygon boundPoly = new WpfPolygon
                {
                    Fill = Brushes.Transparent,
                    Stroke = new WpfSolidColorBrush(WpfColor.FromRgb(220, 38, 38)),
                    StrokeThickness = 2.2,
                    StrokeDashArray = new DoubleCollection { 6, 3 }
                };
                foreach (Point2D p in _boundaryPoints)
                {
                    boundPoly.Points.Add(WorldToScreen(p, cx, cy));
                }
                _canvas.Children.Add(boundPoly);

                // Вершины красной линии (можно перетаскивать мышью в режиме Select)
                for (int i = 0; i < _boundaryPoints.Count; i++)
                {
                    System.Windows.Point sc = WorldToScreen(_boundaryPoints[i], cx, cy);
                    WpfEllipse dot = new WpfEllipse
                    {
                        Width = 10,
                        Height = 10,
                        Fill = new WpfSolidColorBrush(WpfColor.FromRgb(220, 38, 38)),
                        Stroke = Brushes.White,
                        StrokeThickness = 1.8,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(dot, sc.X - 5);
                    Canvas.SetTop(dot, sc.Y - 5);
                    _canvas.Children.Add(dot);
                }
            }

            // 7. Отрисовка текущего строящегося полигона
            if (_currentDrawingPoints.Count > 0)
            {
                Polyline previewLine = new Polyline
                {
                    Stroke = new WpfSolidColorBrush(WpfColor.FromRgb(37, 99, 235)),
                    StrokeThickness = 2.2
                };
                foreach (Point2D p in _currentDrawingPoints)
                {
                    previewLine.Points.Add(WorldToScreen(p, cx, cy));
                }
                _canvas.Children.Add(previewLine);

                // Резиновая нить к курсору мыши
                if (_currentMouseMeters != null)
                {
                    System.Windows.Point lastPt = WorldToScreen(_currentDrawingPoints[^1], cx, cy);
                    System.Windows.Point mousePt = WorldToScreen(_currentMouseMeters, cx, cy);
                    WpfLine rubberBand = new WpfLine
                    {
                        X1 = lastPt.X,
                        Y1 = lastPt.Y,
                        X2 = mousePt.X,
                        Y2 = mousePt.Y,
                        Stroke = new WpfSolidColorBrush(WpfColor.FromRgb(59, 130, 246)),
                        StrokeThickness = 1.6,
                        StrokeDashArray = new DoubleCollection { 4, 3 }
                    };
                    _canvas.Children.Add(rubberBand);
                }

                // Точки-вершины строящегося полигона
                for (int i = 0; i < _currentDrawingPoints.Count; i++)
                {
                    System.Windows.Point sc = WorldToScreen(_currentDrawingPoints[i], cx, cy);
                    bool isFirst = i == 0 && _currentDrawingPoints.Count >= 3;
                    double sz = isFirst ? 11 : 7;
                    WpfEllipse ptDot = new WpfEllipse
                    {
                        Width = sz,
                        Height = sz,
                        Fill = isFirst
                            ? new WpfSolidColorBrush(WpfColor.FromRgb(16, 185, 129))
                            : new WpfSolidColorBrush(WpfColor.FromRgb(37, 99, 235)),
                        Stroke = Brushes.White,
                        StrokeThickness = 1.5
                    };
                    Canvas.SetLeft(ptDot, sc.X - (sz / 2.0));
                    Canvas.SetTop(ptDot, sc.Y - (sz / 2.0));
                    _canvas.Children.Add(ptDot);
                }
            }

            // Обновляем экспликацию площадей в правой панели
            UpdateExplicationValues();
            UpdateZonesList();
        }

        private void DrawEditableZoneVertices(SiteZonePolygon zone, double cx, double cy, WpfColor dotColor)
        {
            foreach (Point2D p in zone.Points)
            {
                System.Windows.Point sc = WorldToScreen(p, cx, cy);
                WpfEllipse dot = new WpfEllipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new WpfSolidColorBrush(dotColor),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, sc.X - 3.5);
                Canvas.SetTop(dot, sc.Y - 3.5);
                _canvas.Children.Add(dot);
            }
        }

        private void DrawBackgroundGrid(double w, double h, double cx, double cy)
        {
            double meterStep = 5.0;
            double pxStep = meterStep * _zoomPxPerM;

            if (pxStep < 15.0)
            {
                meterStep = 20.0;
                pxStep = meterStep * _zoomPxPerM;
            }

            WpfBrush thinGridBrush = new WpfSolidColorBrush(WpfColor.FromRgb(226, 232, 240));
            WpfBrush thickGridBrush = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225));

            double startX = (cx - (_panX * _zoomPxPerM)) % pxStep;
            while (startX < 0) startX += pxStep;
            for (double x = startX; x < w; x += pxStep)
            {
                double m = (x - cx) / _zoomPxPerM + _panX;
                bool isMajor = Math.Abs(m % 20.0) < 0.01;
                WpfLine l = new WpfLine
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = h,
                    Stroke = isMajor ? thickGridBrush : thinGridBrush,
                    StrokeThickness = isMajor ? 1.0 : 0.6
                };
                _canvas.Children.Add(l);
            }

            double startY = (cy + (_panY * _zoomPxPerM)) % pxStep;
            while (startY < 0) startY += pxStep;
            for (double y = startY; y < h; y += pxStep)
            {
                double m = (cy - y) / _zoomPxPerM + _panY;
                bool isMajor = Math.Abs(m % 20.0) < 0.01;
                WpfLine l = new WpfLine
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = w,
                    Y2 = y,
                    Stroke = isMajor ? thickGridBrush : thinGridBrush,
                    StrokeThickness = isMajor ? 1.0 : 0.6
                };
                _canvas.Children.Add(l);
            }
        }

        // =========================================================================
        // МЫШЬ И ИНТЕРАКТИВНОЕ РИСОВАНИЕ + ПЕРЕТАСКИВАНИЕ ВЕРШИН КОНТУРА
        // =========================================================================

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _canvas.Focus();
            System.Windows.Point screenPt = e.GetPosition(_canvas);
            Point2D worldPt = ScreenToWorld(screenPt);
            double cx = _canvas.ActualWidth / 2.0;
            double cy = _canvas.ActualHeight / 2.0;

            if (e.ChangedButton == MouseButton.Left)
            {
                if (_currentTool is EditorTool.DrawBoundary or EditorTool.DrawAsphalt or EditorTool.DrawPaving)
                {
                    // Замыкание при клике рядом с первой точкой (зелёный кружок)
                    if (_currentDrawingPoints.Count >= 3)
                    {
                        System.Windows.Point startScreenPt = WorldToScreen(_currentDrawingPoints[0], cx, cy);
                        if ((screenPt - startScreenPt).Length < 18.0)
                        {
                            FinishCurrentPolygon();
                            return;
                        }
                    }

                    // Двойной клик также завершает и замыкает полигон
                    if (e.ClickCount >= 2 && _currentDrawingPoints.Count >= 3)
                    {
                        FinishCurrentPolygon();
                        return;
                    }

                    _currentDrawingPoints.Add(worldPt);
                    UpdateUndoRedoUI();
                    RedrawCanvas();
                }
                else if (_currentTool == EditorTool.Select)
                {
                    // 1. Проверяем захват вершины красной границы участка
                    for (int i = 0; i < _boundaryPoints.Count; i++)
                    {
                        System.Windows.Point vSc = WorldToScreen(_boundaryPoints[i], cx, cy);
                        if ((screenPt - vSc).Length <= 12.0)
                        {
                            _dragStartSnapshot = CaptureState();
                            _dragStartPoint = _boundaryPoints[i];
                            _draggingBoundaryVertexIndex = i;
                            _canvas.CaptureMouse();
                            return;
                        }
                    }

                    // 2. Проверяем захват вершины пользовательской зоны асфальта/плитки
                    foreach (SiteZonePolygon z in _asphaltZones.Concat(_pavingZones).Where(z => !z.IsDefaultPerimeterRing))
                    {
                        for (int i = 0; i < z.Points.Count; i++)
                        {
                            System.Windows.Point vSc = WorldToScreen(z.Points[i], cx, cy);
                            if ((screenPt - vSc).Length <= 10.0)
                            {
                                _dragStartSnapshot = CaptureState();
                                _dragStartPoint = z.Points[i];
                                _draggingZone = z;
                                _draggingZoneVertexIndex = i;
                                _canvas.CaptureMouse();
                                return;
                            }
                        }
                    }

                    // Иначе — панорамирование левой кнопкой в режиме навигации
                    _lastDragPoint = screenPt;
                }
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                if (_currentDrawingPoints.Count >= 3)
                {
                    // Правый клик при >= 3 точках сразу замыкает полигон
                    FinishCurrentPolygon();
                }
                else if (_currentDrawingPoints.Count > 0)
                {
                    _currentDrawingPoints.RemoveAt(_currentDrawingPoints.Count - 1);
                    UpdateUndoRedoUI();
                    RedrawCanvas();
                }
                else
                {
                    _lastDragPoint = screenPt;
                }
            }
            else if (e.ChangedButton == MouseButton.Middle)
            {
                _lastDragPoint = screenPt;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            System.Windows.Point screenPt = e.GetPosition(_canvas);
            _currentMouseMeters = ScreenToWorld(screenPt);

            _statusCoordsText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "X: {0:F1} м, Y: {1:F1} м | 1 м = {2:F1} px",
                _currentMouseMeters.X,
                _currentMouseMeters.Y,
                _zoomPxPerM);

            // Перетаскивание вершины границы участка
            if (_draggingBoundaryVertexIndex >= 0 && _draggingBoundaryVertexIndex < _boundaryPoints.Count && e.LeftButton == MouseButtonState.Pressed)
            {
                _boundaryPoints[_draggingBoundaryVertexIndex] = _currentMouseMeters;
                RedrawCanvas();
                return;
            }

            // Перетаскивание вершины зоны асфальта/тротуара
            if (_draggingZone != null && _draggingZoneVertexIndex >= 0 && _draggingZoneVertexIndex < _draggingZone.Points.Count && e.LeftButton == MouseButtonState.Pressed)
            {
                _draggingZone.Points[_draggingZoneVertexIndex] = _currentMouseMeters;
                RedrawCanvas();
                return;
            }

            // Панорамирование холста
            if (_lastDragPoint.HasValue &&
                (e.RightButton == MouseButtonState.Pressed ||
                 e.MiddleButton == MouseButtonState.Pressed ||
                 (_currentTool == EditorTool.Select && e.LeftButton == MouseButtonState.Pressed)))
            {
                System.Windows.Vector delta = screenPt - _lastDragPoint.Value;
                _panX -= delta.X / _zoomPxPerM;
                _panY += delta.Y / _zoomPxPerM;
                _lastDragPoint = screenPt;
                RedrawCanvas();
                return;
            }

            if (_currentDrawingPoints.Count > 0)
            {
                RedrawCanvas();
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggingBoundaryVertexIndex >= 0 || _draggingZone != null)
            {
                if (_dragStartSnapshot != null && _dragStartPoint != null)
                {
                    Point2D? finalPoint = null;
                    if (_draggingBoundaryVertexIndex >= 0 && _draggingBoundaryVertexIndex < _boundaryPoints.Count)
                    {
                        finalPoint = _boundaryPoints[_draggingBoundaryVertexIndex];
                    }
                    else if (_draggingZone != null && _draggingZoneVertexIndex >= 0 && _draggingZoneVertexIndex < _draggingZone.Points.Count)
                    {
                        finalPoint = _draggingZone.Points[_draggingZoneVertexIndex];
                    }

                    if (finalPoint != null)
                    {
                        double dx = finalPoint.X - _dragStartPoint.X;
                        double dy = finalPoint.Y - _dragStartPoint.Y;
                        if ((dx * dx) + (dy * dy) > 0.0001)
                        {
                            PushSnapshot(_dragStartSnapshot);
                        }
                    }
                }

                _dragStartSnapshot = null;
                _dragStartPoint = null;
                _draggingBoundaryVertexIndex = -1;
                _draggingZone = null;
                _draggingZoneVertexIndex = -1;
                _canvas.ReleaseMouseCapture();
                RedrawCanvas();
            }
            _lastDragPoint = null;
        }

        private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double factor = e.Delta > 0 ? 1.15 : (1.0 / 1.15);
            _zoomPxPerM = Math.Clamp(_zoomPxPerM * factor, 1.5, 40.0);
            RedrawCanvas();
        }

        private void FinishCurrentPolygon()
        {
            if (_currentDrawingPoints.Count < 3)
            {
                _currentDrawingPoints.Clear();
                UpdateUndoRedoUI();
                RedrawCanvas();
                return;
            }

            PushUndoState();

            List<Point2D> pts = [.. _currentDrawingPoints];
            _currentDrawingPoints.Clear();

            switch (_currentTool)
            {
                case EditorTool.DrawBoundary:
                    _boundaryPoints = pts;
                    _currentTool = EditorTool.Select;
                    break;

                case EditorTool.DrawAsphalt:
                    _asphaltZones.Add(new SiteZonePolygon
                    {
                        Name = $"Асфальтированная зона #{_asphaltZones.Count + 1}",
                        CategoryKey = "ASPHALT",
                        Points = pts
                    });
                    break;

                case EditorTool.DrawPaving:
                    _pavingZones.Add(new SiteZonePolygon
                    {
                        Name = $"Тротуар / мощение #{_pavingZones.Count + 1}",
                        CategoryKey = "PAVING",
                        Points = pts
                    });
                    break;
            }

            UpdateToolSelectionUI();
            UpdateUndoRedoUI();
            RedrawCanvas();
        }

        // =========================================================================
        // ПРЕОБРАЗОВАНИЕ КООРДИНАТ (МИР <-> ЭКРАН)
        // =========================================================================

        private System.Windows.Point WorldToScreen(Point2D pt, double cx, double cy)
        {
            double scX = cx + ((pt.X - _panX) * _zoomPxPerM);
            double scY = cy - ((pt.Y - _panY) * _zoomPxPerM);
            return new System.Windows.Point(scX, scY);
        }

        private Point2D ScreenToWorld(System.Windows.Point sc)
        {
            double cx = _canvas.ActualWidth / 2.0;
            double cy = _canvas.ActualHeight / 2.0;
            double wX = _panX + ((sc.X - cx) / _zoomPxPerM);
            double wY = _panY + ((cy - sc.Y) / _zoomPxPerM);
            return new Point2D(wX, wY);
        }

        // =========================================================================
        // РАСЧЕТ ПЛОЩАДЕЙ И ОБНОВЛЕНИЕ ЭКСПЛИКАЦИИ ЗОН
        // =========================================================================

        private void UpdateExplicationValues()
        {
            double totalSiteArea = CalculatePolygonArea(_boundaryPoints);

            // Учет площадей включенных функциональных зон
            double terrArea = (_chkTerrace?.IsChecked == true) ? Math.Clamp(_buildingWidthM * 0.65, 9.0, 16.0) * 7.0 : 0.0;
            double plazaArea = (_chkFountainPlaza?.IsChecked == true) ? 96.0 : 0.0;
            double kidsArea = (_chkPlayground?.IsChecked == true) ? 45.0 : 0.0;
            double wasteArea = (_chkWasteSite?.IsChecked == true) ? 19.3 : 0.0;
            double dockArea = (_chkServiceDock?.IsChecked == true) ? 63.0 : 0.0;
            double tpArea = (_chkUtilityBlock?.IsChecked == true) ? 15.8 : 0.0;

            double asphaltArea = _asphaltZones.Sum(z => z.CalculateAreaM2()) + dockArea;
            double pavingArea = _pavingZones.Sum(z => z.CalculateAreaM2()) + terrArea + plazaArea + kidsArea + wasteArea;
            double buildingConstructed = _buildingAreaM2 + tpArea;
            double greenArea = Math.Max(0.0, totalSiteArea - buildingConstructed - asphaltArea - pavingArea);

            double bldgPct = totalSiteArea > 0 ? (buildingConstructed / totalSiteArea) * 100.0 : 0.0;
            double asphPct = totalSiteArea > 0 ? (asphaltArea / totalSiteArea) * 100.0 : 0.0;
            double pavPct = totalSiteArea > 0 ? (pavingArea / totalSiteArea) * 100.0 : 0.0;
            double grnPct = totalSiteArea > 0 ? (greenArea / totalSiteArea) * 100.0 : 0.0;

            double kz = totalSiteArea > 0 ? buildingConstructed / totalSiteArea : 0.0;
            double koz = totalSiteArea > 0 ? greenArea / totalSiteArea : 0.0;

            _txtBuildingArea.Text = $"{buildingConstructed:F1} м² ({bldgPct:F1}%)";
            _txtAsphaltArea.Text = $"{asphaltArea:F1} м² ({asphPct:F1}%)";
            _txtPavingArea.Text = $"{pavingArea:F1} м² ({pavPct:F1}%)";
            _txtGreenArea.Text = $"{greenArea:F1} м² ({grnPct:F1}%)";
            _txtTotalArea.Text = $"{totalSiteArea:F1} м² (100.0%)";
            _txtKz.Text = $"{kz:F2} (норма ≤ 0.60)";
            _txtKoz.Text = $"{koz:F2} (норма ≥ 0.20)";
        }

        private void UpdateZonesList()
        {
            _zonesListPanel.Children.Clear();

            int idx = 1;
            foreach (SiteZonePolygon z in _asphaltZones)
            {
                _zonesListPanel.Children.Add(CreateZoneListItem(z, idx++, isAsphalt: true));
            }
            foreach (SiteZonePolygon z in _pavingZones)
            {
                _zonesListPanel.Children.Add(CreateZoneListItem(z, idx++, isAsphalt: false));
            }

            if (_asphaltZones.Count == 0 && _pavingZones.Count == 0)
            {
                _zonesListPanel.Children.Add(new TextBlock
                {
                    Text = "Нет зон покрытий. Нажмите «⬛ Отрисовать асфальтированную зону» и кликайте на холсте.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(100, 116, 139)),
                    FontStyle = FontStyles.Italic
                });
            }
        }

        private UIElement CreateZoneListItem(SiteZonePolygon zone, int num, bool isAsphalt)
        {
            DockPanel dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            Button btnDel = new Button
            {
                Content = "✕",
                Width = 22,
                Height = 20,
                FontSize = 10,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(239, 68, 68)),
                Cursor = Cursors.Hand,
                ToolTip = "Удалить эту зону с генплана"
            };
            btnDel.Click += (_, _) =>
            {
                PushUndoState();
                if (isAsphalt) _asphaltZones.Remove(zone);
                else _pavingZones.Remove(zone);
                RedrawCanvas();
            };
            DockPanel.SetDock(btnDel, WpfDock.Right);
            dp.Children.Add(btnDel);

            TextBlock tb = new TextBlock
            {
                Text = $"{num}. {(isAsphalt ? "⬛" : "🟨")} {zone.Name} — {zone.CalculateAreaM2():F1} м²",
                FontSize = 11.2,
                Foreground = isAsphalt
                    ? new WpfSolidColorBrush(WpfColor.FromRgb(30, 41, 59))
                    : new WpfSolidColorBrush(WpfColor.FromRgb(146, 64, 14)),
                VerticalAlignment = VerticalAlignment.Center
            };
            dp.Children.Add(tb);

            return dp;
        }

        // =========================================================================
        // ПРИМЕНЕНИЕ РЕЗУЛЬТАТА И ФОРМИРОВАНИЕ КОНФИГА
        // =========================================================================

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            double totalSiteArea = CalculatePolygonArea(_boundaryPoints);
            double terrArea = (_chkTerrace?.IsChecked == true) ? Math.Clamp(_buildingWidthM * 0.65, 9.0, 16.0) * 7.0 : 0.0;
            double plazaArea = (_chkFountainPlaza?.IsChecked == true) ? 96.0 : 0.0;
            double kidsArea = (_chkPlayground?.IsChecked == true) ? 45.0 : 0.0;
            double wasteArea = (_chkWasteSite?.IsChecked == true) ? 19.3 : 0.0;
            double dockArea = (_chkServiceDock?.IsChecked == true) ? 63.0 : 0.0;
            double tpArea = (_chkUtilityBlock?.IsChecked == true) ? 15.8 : 0.0;

            double asphaltArea = _asphaltZones.Sum(z => z.CalculateAreaM2()) + dockArea;
            double pavingArea = _pavingZones.Sum(z => z.CalculateAreaM2()) + terrArea + plazaArea + kidsArea + wasteArea;
            double greenArea = Math.Max(0.0, totalSiteArea - _buildingAreaM2 - tpArea - asphaltArea - pavingArea);

            ResultConfig = new SitePlanEditorConfig
            {
                BoundaryPoints = [.. _boundaryPoints],
                AsphaltZones = [.. _asphaltZones],
                PavingZones = [.. _pavingZones],

                HasPerimeterBlindArea = _pavingZones.Any(z => z.IsDefaultPerimeterRing),
                HasPerimeterRoad = _asphaltZones.Any(z => z.IsDefaultPerimeterRing),

                IncludeTerrace = _chkTerrace?.IsChecked == true,
                IncludeFountainPlaza = _chkFountainPlaza?.IsChecked == true,
                IncludeGuestParking = _chkGuestParking?.IsChecked == true,
                IncludeServiceDock = _chkServiceDock?.IsChecked == true,
                IncludeWasteSite = _chkWasteSite?.IsChecked == true,
                IncludePlayground = _chkPlayground?.IsChecked == true,
                IncludeUtilityBlock = _chkUtilityBlock?.IsChecked == true,
                IncludeStaffParking = _chkStaffParking?.IsChecked == true,
                IncludePerimeterFence = _chkPerimeterFence?.IsChecked == true,
                IncludePerimeterTrees = _chkPerimeterTrees?.IsChecked == true,

                BuildingColor = new RevitColor(255, 255, 255),
                AsphaltColor = GetSelectedAsphaltRevitColor(),
                PavingColor = GetSelectedPavingRevitColor(),
                GreenColor = GetSelectedGreenRevitColor(),
                CurbColor = GetSelectedCurbRevitColor(),

                AsphaltTextureName = _cbAsphaltTexture.SelectedItem?.ToString() ?? "Тёмно-серый асфальт",
                PavingTextureName = _cbPavingTexture.SelectedItem?.ToString() ?? "Тротуарная плитка",
                GreenTextureName = _cbGreenTexture.SelectedItem?.ToString() ?? "Изумрудный газон",
                CurbTextureName = _cbCurbTexture.SelectedItem?.ToString() ?? "Бетонный бордюр",

                WhiteBuildingFootprintOnly = _chkWhiteBuilding.IsChecked == true,
                HideInternalWallsAndRooms = _chkWhiteBuilding.IsChecked == true,
                CreateZoneExplicationTable = _chkCreateExplication.IsChecked == true,

                BuildingAreaM2 = _buildingAreaM2,
                AsphaltAreaM2 = asphaltArea,
                PavingAreaM2 = pavingArea,
                GreenAreaM2 = greenArea,
                TotalSiteAreaM2 = totalSiteArea
            };

            string devName = GostStampHelper.FormatNameToGost(_tbDeveloper.Text);
            string chkName = GostStampHelper.FormatNameToGost(_tbChecker.Text);
            string gipName = GostStampHelper.FormatNameToGost(_tbGip.Text);
            string purposeText = _cbPurpose.SelectedItem?.ToString() ?? _initialStamp.BuildingPurpose;
            BuildingPurposeCategory purposeCat = GostStampHelper.ParseBuildingPurposeCategory(purposeText);
            int scaleVal = _cbScale.SelectedIndex switch
            {
                1 => 200,
                2 => 250,
                3 => 300,
                4 => 400,
                5 => 1000,
                _ => 500
            };

            UpdatedGostStamp = _initialStamp with
            {
                RawUserInput = devName,
                FormattedDeveloperName = devName,
                FormattedCheckerName = chkName,
                FormattedNormControllerName = chkName,
                FormattedApproverName = gipName,
                ProjectName = string.IsNullOrWhiteSpace(_tbProjectName.Text) ? "Общественное здание" : _tbProjectName.Text.Trim(),
                ProjectCode = string.IsNullOrWhiteSpace(_tbProjectCode.Text) ? "2026-АР" : _tbProjectCode.Text.Trim(),
                OrganizationName = string.IsNullOrWhiteSpace(_tbOrgName.Text) ? "ТОО «Казгорпроект»" : _tbOrgName.Text.Trim(),
                ProjectAddress = string.IsNullOrWhiteSpace(_tbAddress.Text) ? "г. Алматы, ул. Сатпаева, 22" : _tbAddress.Text.Trim(),
                StudentGroup = _tbGroup.Text?.Trim() ?? string.Empty,
                BuildingPurpose = purposeText,
                PurposeCategory = purposeCat,
                SitePlanScale = scaleVal,
                CustomSitePlanConfig = ResultConfig
            };

            DialogResult = true;
            Close();
        }

        // =========================================================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ГЕОМЕТРИИ И ТЕКСТУРНЫХ КИСТЕЙ WPF
        // =========================================================================

        private static double CalculatePolygonArea(List<Point2D> pts)
        {
            if (pts == null || pts.Count < 3) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < pts.Count; i++)
            {
                Point2D p0 = pts[i];
                Point2D p1 = PointsAt(pts, (i + 1) % pts.Count);
                sum += (p0.X * p1.Y) - (p1.X * p0.Y);
            }
            return Math.Abs(sum) * 0.5;
        }

        private static Point2D PointsAt(List<Point2D> pts, int idx) => pts[idx];

        private static Point2D ComputePolygonCenter(List<Point2D> pts)
        {
            if (pts == null || pts.Count == 0) return new Point2D(0, 0);
            double avgX = pts.Average(p => p.X);
            double avgY = pts.Average(p => p.Y);
            return new Point2D(avgX, avgY);
        }

        private Button CreateToolButton(string text, EditorTool tool)
        {
            Button btn = new Button
            {
                Content = text,
                Height = 30,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 5),
                FontSize = 11.2,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
                Background = Brushes.White,
                Cursor = Cursors.Hand
            };
            btn.Click += (_, _) =>
            {
                _currentTool = tool;
                _currentDrawingPoints.Clear();
                UpdateToolSelectionUI();
                UpdateUndoRedoUI();
                RedrawCanvas();
            };
            return btn;
        }

        private static Button CreateSmallActionButton(string text, RoutedEventHandler handler)
        {
            Button btn = new Button
            {
                Content = text,
                Height = 27,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 5, 5),
                FontSize = 10.8,
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(241, 245, 249)),
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(51, 65, 85)),
                Cursor = Cursors.Hand
            };
            btn.Click += handler;
            return btn;
        }

        private void UpdateToolSelectionUI()
        {
            HighlightButton(_btnToolSelect, _currentTool == EditorTool.Select);
            HighlightButton(_btnToolBoundary, _currentTool == EditorTool.DrawBoundary);
            HighlightButton(_btnToolAsphalt, _currentTool == EditorTool.DrawAsphalt);
            HighlightButton(_btnToolPaving, _currentTool == EditorTool.DrawPaving);

            _toolPromptText.Text = _currentTool switch
            {
                EditorTool.DrawBoundary => "Отрисовка контура участка: кликайте по точкам (Ctrl+Z — отмена точки, клик в зелёную начальную точку — замкнуть).",
                EditorTool.DrawAsphalt => "Отрисовка асфальтированной зоны: кликайте по углам дороги/парковки (Ctrl+Z — отмена точки, клик в зелёную точку — замкнуть).",
                EditorTool.DrawPaving => "Отрисовка тротуара/плитки: кликайте по углам пешеходной зоны (Ctrl+Z — отмена точки, клик в зелёную точку — замкнуть).",
                _ => "Навигация: перетаскивайте вершины мышью, колесо — зум, Ctrl+Z — отменить действие, Ctrl+Y — повторить."
            };
        }

        private static void HighlightButton(Button btn, bool active)
        {
            btn.Background = active
                ? new WpfSolidColorBrush(WpfColor.FromRgb(37, 99, 235))
                : Brushes.White;
            btn.Foreground = active
                ? Brushes.White
                : new WpfSolidColorBrush(WpfColor.FromRgb(30, 41, 59));
            btn.BorderBrush = active
                ? new WpfSolidColorBrush(WpfColor.FromRgb(29, 78, 216))
                : new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225));
        }

        private static TextBlock CreateSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = 12.2,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 8, 0, 4)
            };
        }

        private static Border CreateTextureSwatch(WpfBrush brush)
        {
            return new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                Background = brush,
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(100, 116, 139)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static StackPanel CreateLayerTextureRow(string label, UIElement comboControl, Border swatch)
        {
            StackPanel sp = new StackPanel { Margin = new Thickness(0, 2, 0, 5) };
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.2,
                FontWeight = FontWeights.Medium,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(51, 65, 85)),
                Margin = new Thickness(0, 0, 0, 2)
            });

            DockPanel dp = new DockPanel();
            DockPanel.SetDock(swatch, WpfDock.Right);
            dp.Children.Add(swatch);
            dp.Children.Add(comboControl);
            sp.Children.Add(dp);
            return sp;
        }

        private static DockPanel CreateFieldRow(string label, UIElement control)
        {
            DockPanel dp = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            TextBlock lbl = new TextBlock
            {
                Text = label,
                FontSize = 11.0,
                FontWeight = FontWeights.Medium,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(71, 85, 105)),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 142
            };
            DockPanel.SetDock(lbl, WpfDock.Left);
            dp.Children.Add(lbl);
            dp.Children.Add(control);
            return dp;
        }

        private static TextBox CreateTextBox(string text)
        {
            return new TextBox
            {
                Text = text,
                FontSize = 11.2,
                Padding = new Thickness(5, 3, 5, 3),
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1)
            };
        }

        private static ComboBox CreateStyledComboBox(string[] items, int selectedIndex)
        {
            return new ComboBox
            {
                ItemsSource = items,
                SelectedIndex = Math.Clamp(selectedIndex, 0, items.Length - 1),
                FontSize = 11.2,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock CreateAreaRow(StackPanel parent, string title, WpfBrush colorSample, string fgColorHex, bool isBold = false)
        {
            DockPanel dp = new DockPanel { Margin = new Thickness(0, 1.5, 0, 1.5) };

            Border sample = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                Background = colorSample,
                BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(sample, WpfDock.Left);
            dp.Children.Add(sample);

            TextBlock titleTb = new TextBlock
            {
                Text = title,
                FontSize = 11.2,
                FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(51, 65, 85)),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(titleTb, WpfDock.Left);
            dp.Children.Add(titleTb);

            TextBlock valueTb = new TextBlock
            {
                Text = "0.0 м²",
                FontSize = 11.2,
                FontWeight = isBold ? FontWeights.Bold : FontWeights.SemiBold,
                Foreground = (WpfSolidColorBrush)new BrushConverter().ConvertFrom(fgColorHex)!,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            dp.Children.Add(valueTb);

            parent.Children.Add(dp);
            return valueTb;
        }

        private WpfBrush GetSelectedAsphaltBrush()
        {
            int idx = _cbAsphaltTexture?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => new WpfSolidColorBrush(WpfColor.FromRgb(90, 95, 105)),
                2 => new WpfSolidColorBrush(WpfColor.FromRgb(120, 127, 139)),
                3 => new WpfSolidColorBrush(WpfColor.FromRgb(40, 42, 46)),
                4 => new WpfSolidColorBrush(WpfColor.FromRgb(70, 75, 83)),
                _ => new WpfSolidColorBrush(WpfColor.FromRgb(60, 64, 70))
            };
        }

        private RevitColor GetSelectedAsphaltRevitColor()
        {
            int idx = _cbAsphaltTexture?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => new RevitColor(90, 95, 105),
                2 => new RevitColor(120, 127, 139),
                3 => new RevitColor(40, 42, 46),
                4 => new RevitColor(70, 75, 83),
                _ => new RevitColor(60, 64, 70)
            };
        }

        private WpfBrush GetSelectedPavingBrush()
        {
            int idx = _cbPavingTexture?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => new WpfSolidColorBrush(WpfColor.FromRgb(197, 187, 168)),
                2 => new WpfSolidColorBrush(WpfColor.FromRgb(192, 125, 88)),
                3 => new WpfSolidColorBrush(WpfColor.FromRgb(236, 239, 241)),
                4 => new WpfSolidColorBrush(WpfColor.FromRgb(230, 204, 160)),
                _ => new WpfSolidColorBrush(WpfColor.FromRgb(228, 222, 210))
            };
        }

        private RevitColor GetSelectedPavingRevitColor()
        {
            int idx = _cbPavingTexture?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => new RevitColor(197, 187, 168),
                2 => new RevitColor(192, 125, 88),
                3 => new RevitColor(236, 239, 241),
                4 => new RevitColor(230, 204, 160),
                _ => new RevitColor(228, 222, 210)
            };
        }

        private WpfBrush GetSelectedGreenBrush()
        {
            int idx = _cbGreenTexture?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => new WpfSolidColorBrush(WpfColor.FromRgb(168, 224, 152)),
                2 => new WpfSolidColorBrush(WpfColor.FromRgb(94, 158, 80)),
                3 => new WpfSolidColorBrush(WpfColor.FromRgb(143, 188, 107)),
                _ => new WpfSolidColorBrush(WpfColor.FromRgb(152, 210, 136))
            };
        }

        private RevitColor GetSelectedGreenRevitColor()
        {
            int idx = _cbGreenTexture?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => new RevitColor(168, 224, 152),
                2 => new RevitColor(94, 158, 80),
                3 => new RevitColor(143, 188, 107),
                _ => new RevitColor(152, 210, 136)
            };
        }

        private WpfBrush GetSelectedCurbBrush()
        {
            int idx = _cbCurbTexture?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => new WpfSolidColorBrush(WpfColor.FromRgb(140, 146, 152)),
                2 => new WpfSolidColorBrush(WpfColor.FromRgb(226, 232, 240)),
                _ => new WpfSolidColorBrush(WpfColor.FromRgb(185, 190, 195))
            };
        }

        private RevitColor GetSelectedCurbRevitColor()
        {
            int idx = _cbCurbTexture?.SelectedIndex ?? 0;
            return idx switch
            {
                1 => new RevitColor(140, 146, 152),
                2 => new RevitColor(226, 232, 240),
                _ => new RevitColor(185, 190, 195)
            };
        }

        // =========================================================================
        // СИСТЕМА ОТМЕНЫ (CTRL+Z) И ПОВТОРА (CTRL+Y)
        // =========================================================================

        public void Undo()
        {
            if (_currentDrawingPoints.Count > 0)
            {
                _currentDrawingPoints.RemoveAt(_currentDrawingPoints.Count - 1);
                _toolPromptText.Text = $"Отменена последняя точка контура. Точек в текущем полигоне: {_currentDrawingPoints.Count}";
                UpdateUndoRedoUI();
                RedrawCanvas();
                return;
            }

            if (_undoStack.Count > 0)
            {
                _redoStack.Push(CaptureState());
                EditorHistoryState prevState = _undoStack.Pop();
                RestoreState(prevState);
                _toolPromptText.Text = "Действие успешно отменено (Ctrl+Z).";
                UpdateUndoRedoUI();
                RedrawCanvas();
            }
        }

        public void Redo()
        {
            if (_redoStack.Count > 0)
            {
                _undoStack.Push(CaptureState());
                EditorHistoryState nextState = _redoStack.Pop();
                RestoreState(nextState);
                _toolPromptText.Text = "Действие повторено (Ctrl+Y).";
                UpdateUndoRedoUI();
                RedrawCanvas();
            }
        }

        private EditorHistoryState CaptureState()
        {
            return EditorHistoryState.CreateSnapshot(
                _boundaryPoints,
                _asphaltZones,
                _pavingZones,
                _chkTerrace?.IsChecked ?? true,
                _chkFountainPlaza?.IsChecked ?? true,
                _chkGuestParking?.IsChecked ?? true,
                _chkServiceDock?.IsChecked ?? true,
                _chkWasteSite?.IsChecked ?? true,
                _chkPlayground?.IsChecked ?? true,
                _chkUtilityBlock?.IsChecked ?? true,
                _chkStaffParking?.IsChecked ?? true,
                _chkPerimeterFence?.IsChecked ?? true,
                _chkPerimeterTrees?.IsChecked ?? true);
        }

        private void PushUndoState()
        {
            PushSnapshot(CaptureState());
        }

        private void PushSnapshot(EditorHistoryState snapshot)
        {
            _undoStack.Push(snapshot);
            if (_undoStack.Count > 50)
            {
                var list = _undoStack.ToList();
                _undoStack.Clear();
                for (int i = Math.Min(49, list.Count - 1); i >= 0; i--)
                {
                    _undoStack.Push(list[i]);
                }
            }
            _redoStack.Clear();
            UpdateUndoRedoUI();
        }

        private void RestoreState(EditorHistoryState state)
        {
            _isRestoringState = true;
            try
            {
                _boundaryPoints = state.BoundaryPoints.Select(p => new Point2D(p.X, p.Y)).ToList();
                _asphaltZones.Clear();
                foreach (var z in state.AsphaltZones)
                {
                    _asphaltZones.Add(EditorHistoryState.CloneZone(z));
                }
                _pavingZones.Clear();
                foreach (var z in state.PavingZones)
                {
                    _pavingZones.Add(EditorHistoryState.CloneZone(z));
                }

                if (_chkTerrace != null) _chkTerrace.IsChecked = state.IncludeTerrace;
                if (_chkFountainPlaza != null) _chkFountainPlaza.IsChecked = state.IncludeFountainPlaza;
                if (_chkGuestParking != null) _chkGuestParking.IsChecked = state.IncludeGuestParking;
                if (_chkServiceDock != null) _chkServiceDock.IsChecked = state.IncludeServiceDock;
                if (_chkWasteSite != null) _chkWasteSite.IsChecked = state.IncludeWasteSite;
                if (_chkPlayground != null) _chkPlayground.IsChecked = state.IncludePlayground;
                if (_chkUtilityBlock != null) _chkUtilityBlock.IsChecked = state.IncludeUtilityBlock;
                if (_chkStaffParking != null) _chkStaffParking.IsChecked = state.IncludeStaffParking;
                if (_chkPerimeterFence != null) _chkPerimeterFence.IsChecked = state.IncludePerimeterFence;
                if (_chkPerimeterTrees != null) _chkPerimeterTrees.IsChecked = state.IncludePerimeterTrees;
            }
            finally
            {
                _isRestoringState = false;
            }
        }

        private void UpdateUndoRedoUI()
        {
            if (_btnUndo == null || _btnRedo == null) return;

            bool canUndo = _currentDrawingPoints.Count > 0 || _undoStack.Count > 0;
            bool canRedo = _redoStack.Count > 0;

            _btnUndo.IsEnabled = canUndo;
            _btnUndo.Opacity = canUndo ? 1.0 : 0.45;

            _btnRedo.IsEnabled = canRedo;
            _btnRedo.Opacity = canRedo ? 1.0 : 0.45;
        }

        private void SitePlanEditorWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Если фокус находится внутри текстового поля ввода (например, реквизиты штампа),
            // стандартный TextBox обрабатывает свой собственный текстовый Ctrl+Z / Ctrl+Y.
            if (e.OriginalSource is TextBox)
            {
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (e.Key == Key.Z)
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    {
                        Redo();
                    }
                    else
                    {
                        Undo();
                    }
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Y)
                {
                    Redo();
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Back && _currentDrawingPoints.Count > 0)
            {
                Undo();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_currentDrawingPoints.Count > 0)
                {
                    _currentDrawingPoints.Clear();
                    _toolPromptText.Text = "Построение контура отменено.";
                    UpdateUndoRedoUI();
                    RedrawCanvas();
                    e.Handled = true;
                }
                else if (_currentTool != EditorTool.Select)
                {
                    _currentTool = EditorTool.Select;
                    UpdateToolSelectionUI();
                    RedrawCanvas();
                    e.Handled = true;
                }
            }
        }

        private CheckBox CreateZoneCheckBox(string text, bool defaultChecked, string tip)
        {
            CheckBox chk = new CheckBox
            {
                Content = text,
                IsChecked = defaultChecked,
                FontSize = 11.2,
                FontWeight = FontWeights.Medium,
                Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 3, 0, 3),
                Cursor = Cursors.Hand,
                ToolTip = tip
            };
            chk.Checked += (_, _) =>
            {
                if (_isRestoringState) return;
                PushUndoState();
                UpdateExplicationValues();
                RedrawCanvas();
            };
            chk.Unchecked += (_, _) =>
            {
                if (_isRestoringState) return;
                PushUndoState();
                UpdateExplicationValues();
                RedrawCanvas();
            };
            return chk;
        }

        private void SetAllFunctionalZones(bool isEnabled)
        {
            _chkTerrace.IsChecked = isEnabled;
            _chkFountainPlaza.IsChecked = isEnabled;
            _chkGuestParking.IsChecked = isEnabled;
            _chkServiceDock.IsChecked = isEnabled;
            _chkWasteSite.IsChecked = isEnabled;
            _chkPlayground.IsChecked = isEnabled;
            _chkUtilityBlock.IsChecked = isEnabled;
            _chkStaffParking.IsChecked = isEnabled;
            _chkPerimeterFence.IsChecked = isEnabled;
            _chkPerimeterTrees.IsChecked = isEnabled;
            UpdateExplicationValues();
            RedrawCanvas();
        }

        private void DrawFunctionalZonesPreview(double cx, double cy)
        {
            double halfW = _buildingWidthM / 2.0;
            double halfL = _buildingLengthM / 2.0;

            // 1. Летняя веранда (если включена)
            if (_chkTerrace?.IsChecked == true)
            {
                double terrW = Math.Clamp(_buildingWidthM * 0.65, 9.0, 16.0);
                double terrH = 7.0;
                double tx0 = -terrW / 2.0;
                double ty0 = -halfL - 7.5 - terrH;
                DrawPreviewRectZone(tx0, ty0, terrW, terrH, cx, cy,
                    new WpfSolidColorBrush(WpfColor.FromArgb(220, 245, 230, 211)),
                    new WpfSolidColorBrush(WpfColor.FromRgb(180, 83, 9)),
                    "☕ Летняя веранда (№ 2)");
            }

            // 2. Входной сквер с фонтаном (если включен)
            if (_chkFountainPlaza?.IsChecked == true)
            {
                double plzW = 12.0;
                double plzH = 8.0;
                double px0 = Math.Min(halfW + 1.0, 12.0);
                double py0 = -halfL - 7.5 - plzH;
                DrawPreviewRectZone(px0, py0, plzW, plzH, cx, cy,
                    new WpfSolidColorBrush(WpfColor.FromArgb(200, 224, 242, 254)),
                    new WpfSolidColorBrush(WpfColor.FromRgb(2, 132, 199)),
                    "⛲ Входной сквер (№ 3)");
            }

            // 3. Детский игровой уголок (если включен)
            if (_chkPlayground?.IsChecked == true)
            {
                double kidW = 7.5;
                double kidH = 6.0;
                double kx0 = -halfW - 7.5 - kidW;
                double ky0 = -halfL;
                DrawPreviewRectZone(kx0, ky0, kidW, kidH, cx, cy,
                    new WpfSolidColorBrush(WpfColor.FromArgb(200, 254, 240, 138)),
                    new WpfSolidColorBrush(WpfColor.FromRgb(202, 138, 4)),
                    "🎠 Детский уголок (№ 7)");
            }

            // 4. Хоздвор кухни (если включен)
            if (_chkServiceDock?.IsChecked == true)
            {
                double dockW = 9.0;
                double dockH = 7.0;
                double dx0 = -dockW / 2.0;
                double dy0 = halfL + 7.5;
                DrawPreviewRectZone(dx0, dy0, dockW, dockH, cx, cy,
                    new WpfSolidColorBrush(WpfColor.FromArgb(200, 203, 213, 225)),
                    new WpfSolidColorBrush(WpfColor.FromRgb(71, 85, 105)),
                    "🚚 Хоздвор кухни (№ 5)");
            }

            // 5. Площадка ТБО (если включена)
            if (_chkWasteSite?.IsChecked == true)
            {
                double wW = 5.5;
                double wH = 3.5;
                double wx0 = -halfW - 6.0;
                double wy0 = halfL + 7.5;
                DrawPreviewRectZone(wx0, wy0, wW, wH, cx, cy,
                    new WpfSolidColorBrush(WpfColor.FromArgb(200, 254, 202, 202)),
                    new WpfSolidColorBrush(WpfColor.FromRgb(220, 38, 38)),
                    "🗑️ ТБО (№ 6)");
            }

            // 6. Инженерный блок ТП (если включен)
            if (_chkUtilityBlock?.IsChecked == true)
            {
                double tpW = 4.5;
                double tpH = 3.5;
                double tpx0 = halfW + 7.5 + 1.0;
                double tpy0 = halfL + 4.0;
                DrawPreviewRectZone(tpx0, tpy0, tpW, tpH, cx, cy,
                    new WpfSolidColorBrush(WpfColor.FromArgb(200, 226, 232, 240)),
                    new WpfSolidColorBrush(WpfColor.FromRgb(100, 116, 139)),
                    "⚡ ТП / блок (№ 8)");
            }

            // 7. Деревья по периметру (если включены)
            if (_chkPerimeterTrees?.IsChecked == true && _boundaryPoints.Count >= 3)
            {
                DrawPerimeterTreesPreview(cx, cy);
            }
        }

        private void DrawPreviewRectZone(double x0, double y0, double w, double h, double cx, double cy, WpfBrush fill, WpfBrush stroke, string label)
        {
            List<Point2D> pts =
            [
                new Point2D(x0, y0),
                new Point2D(x0 + w, y0),
                new Point2D(x0 + w, y0 + h),
                new Point2D(x0, y0 + h)
            ];

            WpfPolygon poly = new WpfPolygon
            {
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1.4,
                IsHitTestVisible = false
            };
            foreach (Point2D p in pts)
            {
                poly.Points.Add(WorldToScreen(p, cx, cy));
            }
            _canvas.Children.Add(poly);

            Point2D centerPt = new Point2D(x0 + (w / 2.0), y0 + (h / 2.0));
            System.Windows.Point scPt = WorldToScreen(centerPt, cx, cy);

            Border badge = new Border
            {
                Background = new WpfSolidColorBrush(WpfColor.FromArgb(220, 255, 255, 255)),
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                IsHitTestVisible = false
            };
            badge.Child = new TextBlock
            {
                Text = label,
                FontSize = 9.0,
                FontWeight = FontWeights.SemiBold,
                Foreground = stroke
            };
            Canvas.SetLeft(badge, scPt.X - 35);
            Canvas.SetTop(badge, scPt.Y - 8);
            _canvas.Children.Add(badge);
        }

        private void DrawPerimeterTreesPreview(double cx, double cy)
        {
            WpfBrush treeBrush = new WpfSolidColorBrush(WpfColor.FromArgb(220, 34, 197, 94));
            WpfBrush treeStroke = new WpfSolidColorBrush(WpfColor.FromRgb(21, 128, 61));

            for (int i = 0; i < _boundaryPoints.Count; i++)
            {
                Point2D p0 = _boundaryPoints[i];
                Point2D p1 = _boundaryPoints[(i + 1) % _boundaryPoints.Count];
                double dx = p1.X - p0.X;
                double dy = p1.Y - p0.Y;
                double len = Math.Sqrt((dx * dx) + (dy * dy));
                if (len < 6.0) continue;

                double step = 8.0;
                double nx = -dy / len;
                double ny = dx / len;

                for (double t = 4.0; t < len - 4.0; t += step)
                {
                    double px = p0.X + (dx * t / len) + (nx * 2.5);
                    double py = p0.Y + (dy * t / len) + (ny * 2.5);
                    System.Windows.Point sc = WorldToScreen(new Point2D(px, py), cx, cy);

                    WpfEllipse treeCircle = new WpfEllipse
                    {
                        Width = 10,
                        Height = 10,
                        Fill = treeBrush,
                        Stroke = treeStroke,
                        StrokeThickness = 1.0,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(treeCircle, sc.X - 5);
                    Canvas.SetTop(treeCircle, sc.Y - 5);
                    _canvas.Children.Add(treeCircle);
                }
            }
        }
    }
}
