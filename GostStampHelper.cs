using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;
using WpfGrid = System.Windows.Controls.Grid;

namespace RevitViewPlacer
{
    /// <summary>
    /// Стили генеративного дизайна генерального плана.
    /// </summary>
    public enum SitePlanStyle
    {
        RandomProcedural = 0, // 🎲 Случайный (Генеративный алгоритм)
        FrontPlaza = 1,       // 🏛️ Представительский (парадная площадь перед главным фасадом)
        BusinessHub = 2,      // 🚗 Деловой (расширенная парковка и кольцевой проезд)
        GreenCampus = 3,      // 🌳 Парковый кампус (максимум озеленения и прогулочных аллей)
        CornerHub = 4         // 📐 Угловой хаб (компактный въезд и парк)
    }

    /// <summary>
    /// Форма кадастровой границы участка генерального плана (красная линия по ГОСТ 21.508-93 / СН РК).
    /// </summary>
    public enum SiteBoundaryShape
    {
        CornerLot = 0,    // 📐 Угловой участок со скосом улиц (по образцу ГОСТ — 7 вершин)
        Trapezoidal = 1,  // 📐 Трапециевидный участок (со скошенным проездом — 6 вершин)
        Polygonal = 2,    // 📐 Сложный многоугольный (с фасками и уступами — 8 вершин)
        Rectangular = 3,  // 📐 Прямоугольный классический (4 вершины)
        Random = 4        // 🎲 Случайная форма (процедурный кадастровый контур)
    }

    /// <summary>
    /// Категория функционального назначения проектируемого объекта для генерации генплана по СН РК.
    /// </summary>
    public enum BuildingPurposeCategory
    {
        Residential = 0,   // Жилое здание (многоквартирный дом / коттедж)
        Restaurant = 1,    // Ресторан / Кафе / Предприятие питания
        Retail = 2,        // Торговый центр / Супермаркет / Магазин
        Office = 3,        // Бизнес-центр / Офисное здание
        Hotel = 4,         // Гостиница / Отель
        Education = 5,     // Детский сад / Школа / Образовательное учреждение
        Industrial = 6     // Складской комплекс / Производство
    }

    /// <summary>
    /// Элемент списка видов и специальных листов для интерактивного выбора пользователем перед созданием комплекта по ГОСТ РК.
    /// </summary>
    public sealed class SheetCandidateItem
    {
        public ElementId ViewId { get; init; } = ElementId.InvalidElementId;
        public ElementId ExistingSheetId { get; init; } = ElementId.InvalidElementId;
        public string VirtualKey { get; init; } = string.Empty;
        public string CategoryBadge { get; init; } = string.Empty;
        public string ViewName { get; init; } = string.Empty;
        public string BaseGostTitle { get; init; } = string.Empty;
        public string SheetTitlePreview { get; set; } = string.Empty;
        public string ExistingSheetBadge { get; init; } = string.Empty;
        public ViewType ViewType { get; init; } = ViewType.Undefined;
        public int SortGroup { get; init; }
        public double SortElevation { get; init; }
        public bool IsCoreArView { get; init; } = true;
        public bool IsSelected { get; set; } = true;
        public bool SupportsScale { get; init; } = true;
        public int SelectedScale { get; set; } = 100;
    }

    /// <summary>
    /// Данные для заполнения основной надписи (штампа Форма 3) по ГОСТ 21.101-97 / СН РК, выбору создаваемых листов и их масштабов.
    /// </summary>
    public sealed record GostStampData(
        string RawUserInput,
        string FormattedDeveloperName,
        string FormattedCheckerName,
        string FormattedNormControllerName,
        string FormattedApproverName,
        string OrganizationName,
        string ProjectName,
        string ProjectCode,
        string IssueDateGost,
        SitePlanStyle SelectedSitePlanStyle = SitePlanStyle.RandomProcedural,
        SiteBoundaryShape SelectedBoundaryShape = SiteBoundaryShape.CornerLot,
        string BuildingPurpose = "Ресторан",
        BuildingPurposeCategory PurposeCategory = BuildingPurposeCategory.Restaurant,
        string StageCode = "РП",
        bool IncludeSitePlanSheet = true,
        bool IncludeGeneralDataSheet = true,
        bool CreateAutoSection1 = true,
        bool CreateAutoSection2 = true,
        bool CreateAutoElevSouth = true,
        bool CreateAutoElevNorth = true,
        bool CreateAutoElevEast = true,
        bool CreateAutoElevWest = true,
        HashSet<long>? SelectedViewIds = null,
        List<SheetCandidateItem>? AllCandidates = null,
        int SitePlanScale = 500,
        int AutoSection1Scale = 75,
        int AutoSection2Scale = 50,
        int AutoElevSouthScale = 100,
        int AutoElevNorthScale = 100,
        int AutoElevEastScale = 100,
        int AutoElevWestScale = 100,
        Dictionary<long, int>? CustomViewScales = null,
        bool ReplaceRoomTagsWithAdsk = true,
        string PreferredRoomTagType = "Номер Имя",
        bool RenumberRoomsSequentially = true,
        string RoomNumberingMode = "PerFloor",
        SitePlanEditorConfig? CustomSitePlanConfig = null,
        string ProjectAddress = "г. Алматы, ул. Сатпаева, 22",
        string StudentGroup = "",
        bool IncludeCoverSheet = true,
        bool IncludeSituationPlanSheet = true,
        bool IncludeRenderSheet = true);

    /// <summary>
    /// Отвечает за диалоговое окно запроса реквизитов штампа по ГОСТ 21.101-97 (СТ РК),
    /// интерактивный выбор видов для создания листов и заполнение основной надписи Форма 3.
    /// </summary>
    public static class GostStampHelper
    {
        public const string GostStampComment = "ГОСТ_Штамп_Текст";

        private static string _lastEnteredName = "Турсынов И.Б.";
        private static string _lastCheckerName = "Старков А.А.";
        private static string _lastGipName = "Турсынов И.Б.";
        private static string _lastNormName = "Старков А.А.";
        private static string _lastOrganization = "ТОО «Казгорпроект»";
        private static string _lastProjectName = "Общественное здание";
        private static string _lastProjectCode = "2026-АР";
        private static string _lastStageCode = "РП";
        private static string _lastAddress = "г. Алматы, ул. Сатпаева, 22";
        private static string _lastGroup = "";

        public static string FormatNameToGost(string rawInput) => FormatNameByGost(rawInput);

        public static GostStampData GetDefaultGostStampData(Document doc)
        {
            string defaultAuthor = _lastEnteredName;
            string? existingAuthor = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_AUTHOR)?.AsString();
            if (!string.IsNullOrWhiteSpace(existingAuthor) &&
                !existingAuthor.Contains("Фамилия", StringComparison.OrdinalIgnoreCase) &&
                !existingAuthor.Equals("Автор", StringComparison.OrdinalIgnoreCase))
            {
                defaultAuthor = existingAuthor;
            }

            string defaultOrg = _lastOrganization;
            string? existingOrg = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_ORGANIZATION_NAME)?.AsString();
            if (!string.IsNullOrWhiteSpace(existingOrg) &&
                !existingOrg.Contains("Название организации", StringComparison.OrdinalIgnoreCase))
            {
                defaultOrg = existingOrg;
            }

            string defaultBuilding = _lastProjectName;
            string? existingBuilding = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_BUILDING_NAME)?.AsString();
            if (!string.IsNullOrWhiteSpace(existingBuilding) &&
                !existingBuilding.Contains("Наименование здания", StringComparison.OrdinalIgnoreCase))
            {
                defaultBuilding = existingBuilding;
            }

            string defaultAddress = _lastAddress;
            string? existingAddress = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_ADDRESS)?.AsString();
            if (!string.IsNullOrWhiteSpace(existingAddress) &&
                !existingAddress.Contains("Enter address", StringComparison.OrdinalIgnoreCase) &&
                !existingAddress.Contains("Адрес проекта", StringComparison.OrdinalIgnoreCase))
            {
                defaultAddress = existingAddress;
            }

            return new GostStampData(
                RawUserInput: defaultAuthor,
                FormattedDeveloperName: FormatNameByGost(defaultAuthor),
                FormattedCheckerName: FormatNameByGost(_lastCheckerName),
                FormattedNormControllerName: FormatNameByGost(_lastNormName),
                FormattedApproverName: FormatNameByGost(_lastGipName),
                OrganizationName: defaultOrg,
                ProjectName: defaultBuilding,
                ProjectCode: _lastProjectCode,
                IssueDateGost: DateTime.Now.ToString("MM.yy", CultureInfo.InvariantCulture),
                StageCode: _lastStageCode,
                ProjectAddress: defaultAddress,
                StudentGroup: _lastGroup);
        }

        public static void SaveLastGostStampData(GostStampData data)
        {
            _lastEnteredName = data.FormattedDeveloperName;
            _lastCheckerName = data.FormattedCheckerName;
            _lastGipName = data.FormattedApproverName;
            _lastNormName = data.FormattedNormControllerName;
            _lastOrganization = data.OrganizationName;
            _lastProjectName = data.ProjectName;
            _lastProjectCode = data.ProjectCode;
            _lastStageCode = data.StageCode;
            _lastAddress = data.ProjectAddress;
            _lastGroup = data.StudentGroup;
        }

        /// <summary>
        /// Показывает диалоговое окно перед созданием листов (с интерактивным выбором видов) и возвращает данные по ГОСТ РК.
        /// Возвращает null, если пользователь отменил ввод.
        /// </summary>
        public static GostStampData? PromptUserForGostStampData(Document doc, bool showViewSelector = true)
        {
            string defaultAuthor = _lastEnteredName;
            if (string.IsNullOrWhiteSpace(defaultAuthor))
            {
                string? existingAuthor = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_AUTHOR)?.AsString();
                if (!string.IsNullOrWhiteSpace(existingAuthor) &&
                    !existingAuthor.Contains("Фамилия", StringComparison.OrdinalIgnoreCase) &&
                    !existingAuthor.Equals("Автор", StringComparison.OrdinalIgnoreCase))
                {
                    defaultAuthor = existingAuthor;
                }
            }

            string defaultOrg = _lastOrganization;
            string? existingOrg = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_ORGANIZATION_NAME)?.AsString();
            if (!string.IsNullOrWhiteSpace(existingOrg) &&
                !existingOrg.Contains("Название организации", StringComparison.OrdinalIgnoreCase))
            {
                defaultOrg = existingOrg;
            }

            string defaultBuilding = _lastProjectName;
            string? existingBuilding = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_BUILDING_NAME)?.AsString();
            if (!string.IsNullOrWhiteSpace(existingBuilding) &&
                !existingBuilding.Contains("Наименование здания", StringComparison.OrdinalIgnoreCase))
            {
                defaultBuilding = existingBuilding;
            }

            string defaultAddress = _lastAddress;
            string? existingAddress = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_ADDRESS)?.AsString();
            if (!string.IsNullOrWhiteSpace(existingAddress) &&
                !existingAddress.Contains("Enter address", StringComparison.OrdinalIgnoreCase) &&
                !existingAddress.Contains("Адрес проекта", StringComparison.OrdinalIgnoreCase))
            {
                defaultAddress = existingAddress;
            }

            List<SheetCandidateItem> candidates = showViewSelector
                ? CollectSheetCandidates(doc)
                : [];

            GostAuthorInputWindow dialog = new GostAuthorInputWindow(
                doc,
                defaultAuthor,
                _lastCheckerName,
                _lastGipName,
                _lastNormName,
                defaultOrg,
                defaultBuilding,
                _lastProjectCode,
                _lastStageCode,
                candidates,
                showViewSelector,
                defaultAddress,
                _lastGroup);

            bool? dialogResult = dialog.ShowDialog();
            if (dialogResult != true || dialog.ResultData == null)
            {
                return null;
            }

            _lastEnteredName = dialog.ResultData.FormattedDeveloperName;
            _lastCheckerName = dialog.ResultData.FormattedCheckerName;
            _lastGipName = dialog.ResultData.FormattedApproverName;
            _lastNormName = dialog.ResultData.FormattedNormControllerName;
            _lastOrganization = dialog.ResultData.OrganizationName;
            _lastProjectName = dialog.ResultData.ProjectName;
            _lastProjectCode = dialog.ResultData.ProjectCode;
            _lastStageCode = dialog.ResultData.StageCode;
            _lastAddress = dialog.ResultData.ProjectAddress;
            _lastGroup = dialog.ResultData.StudentGroup;

            return dialog.ResultData;
        }

        /// <summary>
        /// Собирает все доступные виды проекта (планы этажей, разрезы, фасады, потолки, 3D) и специальные листы ГОСТ РК
        /// для интерактивного отображения с галочками, чтобы пользователь видел, из каких видов создаются листы, и мог убрать лишние.
        /// </summary>
        public static List<SheetCandidateItem> CollectSheetCandidates(Document doc)
        {
            List<SheetCandidateItem> result = [];
            BoundingBoxXYZ? buildingBox = SheetLayoutEngine.ComputeBuildingWorldBoundingBox(doc);

            // 0. Лист 1: Обложка (титульный лист альбома проектов по ГОСТ 21.101-97)
            result.Add(new SheetCandidateItem
            {
                VirtualKey = "SHEET_COVER",
                CategoryBadge = "ОБЛОЖКА",
                ViewName = "Титульный лист альбома (семейство без штампа, крупный текст)",
                BaseGostTitle = "Обложка",
                SheetTitlePreview = "Лист 1: Обложка альбома (Имя автора и группа студента)",
                SortGroup = -4,
                SortElevation = 0,
                IsCoreArView = true,
                IsSelected = true,
                SupportsScale = false,
                SelectedScale = 0
            });

            // 1. Лист 2: Специальный заглавный лист: Общие данные по ГОСТ 21.101-97 и СН РК 1.02-03-2022
            result.Add(new SheetCandidateItem
            {
                VirtualKey = "SHEET_GENERAL_DATA",
                CategoryBadge = "ОБЩИЕ ДАННЫЕ",
                ViewName = "Заглавный лист (Ведомость чертежей, ТЭП, Общие указания РК)",
                BaseGostTitle = "Общие данные по рабочим чертежам",
                SheetTitlePreview = "Лист 2: Общие данные по рабочим чертежам (ГОСТ 21.101-97 / СН РК)",
                SortGroup = -3,
                SortElevation = 0,
                IsCoreArView = true,
                IsSelected = true,
                SupportsScale = false,
                SelectedScale = 0
            });

            // 2. Лист 3: Ситуационная схема по ГОСТ 21.508-93 (М 1:5000)
            result.Add(new SheetCandidateItem
            {
                VirtualKey = "SHEET_SITUATION_PLAN",
                CategoryBadge = "СИТУАЦИЯ",
                ViewName = "Ситуационная схема расположения (М 1:5000, спутниковая карта / ГОСТ)",
                BaseGostTitle = "Ситуационная схема",
                SheetTitlePreview = "Лист 3: Ситуационная схема (М 1:5000, характеристики расположения)",
                SortGroup = -2,
                SortElevation = 0,
                IsCoreArView = true,
                IsSelected = true,
                SupportsScale = true,
                SelectedScale = 5000
            });

            // 3. Лист 4: Генеральный план по ГОСТ 21.508-93 и СН РК 3.01-01-2013
            result.Add(new SheetCandidateItem
            {
                VirtualKey = "SHEET_SITE_PLAN",
                CategoryBadge = "ГЕНПЛАН ГП-1",
                ViewName = "Генеральный план и благоустройство территории (3D + 2D)",
                BaseGostTitle = "Схема генерального плана",
                SheetTitlePreview = "Лист 4: Схема генерального плана (М 1:500), экспликация и ТЭП по СН РК 3.01-01",
                SortGroup = -1,
                SortElevation = 0,
                IsCoreArView = true,
                IsSelected = true,
                SupportsScale = true,
                SelectedScale = 500
            });

            // Карта уже размещенных видов -> Лист
            Dictionary<ElementId, ViewSheet> viewToSheetMap = new Dictionary<ElementId, ViewSheet>();
            List<ViewSheet> allSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate)
                .ToList();

            foreach (ViewSheet s in allSheets)
            {
                foreach (ElementId vpId in s.GetAllViewports())
                {
                    if (doc.GetElement(vpId) is Viewport vp && vp.ViewId != ElementId.InvalidElementId)
                    {
                        viewToSheetMap[vp.ViewId] = s;
                    }
                }
            }

            // Собираем все печатаемые виды модели (кроме служебных видов генплана ГП_3D)
            List<View> allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate
                            && v.CanBePrinted
                            && IsSupportedViewType(v.ViewType)
                            && !string.IsNullOrWhiteSpace(v.Name)
                            && !v.Name.StartsWith("ГП_", StringComparison.OrdinalIgnoreCase)
                            && !v.Name.Contains("Генеральный план", StringComparison.OrdinalIgnoreCase)
                            && !v.Name.Contains("Ведомость рабочих чертежей", StringComparison.OrdinalIgnoreCase))
                .ToList();

            bool hasSection1 = false;
            bool hasSection2 = false;
            bool hasSouthElevation = false;
            bool hasNorthElevation = false;
            bool hasEastElevation = false;
            bool hasWestElevation = false;

            (string nStart, string nEnd, string lStart, string lEnd) = SheetLayoutEngine.GetExtremeGridLabels(doc);

            foreach (View v in allViews)
            {
                string vRawName = v.Name ?? string.Empty;
                if (v.ViewType == ViewType.Section)
                {
                    if (Regex.IsMatch(vRawName, @"(?:1[-–—]1|1\b|Продольн)", RegexOptions.IgnoreCase))
                        hasSection1 = true;
                    if (Regex.IsMatch(vRawName, @"(?:2[-–—]2|2\b|Поперечн)", RegexOptions.IgnoreCase))
                        hasSection2 = true;
                }
                else if (v.ViewType == ViewType.Elevation)
                {
                    if (ElevationSheetBuilder.IsElevationMatchingSide(v, "SOUTH", nStart, nEnd, lStart, lEnd))
                        hasSouthElevation = true;
                    if (ElevationSheetBuilder.IsElevationMatchingSide(v, "NORTH", nStart, nEnd, lStart, lEnd))
                        hasNorthElevation = true;
                    if (ElevationSheetBuilder.IsElevationMatchingSide(v, "EAST", nStart, nEnd, lStart, lEnd))
                        hasEastElevation = true;
                    if (ElevationSheetBuilder.IsElevationMatchingSide(v, "WEST", nStart, nEnd, lStart, lEnd))
                        hasWestElevation = true;
                }

                (int sortGroup, string badge, bool isCore, bool defaultSelected) = ClassifyViewCandidate(v);
                int currentScale = v.Scale > 0 ? v.Scale : 100;
                int estimatedScale = SheetLayoutEngine.EstimateGostScaleForView(doc, v, buildingBox);
                int initialScale = currentScale > 0 ? currentScale : estimatedScale;
                string baseTitle = SheetLayoutEngine.GetGostStandardViewTitle(v, scale: 0);
                string gostTitle = SheetLayoutEngine.GetGostStandardViewTitle(v, scale: initialScale);

                string existingBadge = string.Empty;
                ElementId existingSheetId = ElementId.InvalidElementId;
                if (viewToSheetMap.TryGetValue(v.Id, out ViewSheet? existingSheet))
                {
                    existingSheetId = existingSheet.Id;
                    existingBadge = $"[На листе {existingSheet.SheetNumber}]";
                }

                double elev = v.GenLevel?.Elevation ?? 0.0;
                string vName = v.Name ?? string.Empty;
                string cleanDisplayViewName = vName.StartsWith("АР_", StringComparison.OrdinalIgnoreCase)
                    ? vName[3..].Trim()
                    : vName;

                bool supportsScale = v.ViewType != ViewType.ThreeD;

                result.Add(new SheetCandidateItem
                {
                    ViewId = v.Id,
                    ExistingSheetId = existingSheetId,
                    CategoryBadge = badge,
                    ViewName = cleanDisplayViewName,
                    BaseGostTitle = baseTitle,
                    SheetTitlePreview = $"Лист АР: {gostTitle} (по ГОСТ 21.501 / СН РК)",
                    ExistingSheetBadge = existingBadge,
                    ViewType = v.ViewType,
                    SortGroup = sortGroup,
                    SortElevation = elev,
                    IsCoreArView = isCore,
                    IsSelected = defaultSelected,
                    SupportsScale = supportsScale,
                    SelectedScale = supportsScale ? initialScale : 0
                });
            }

            // Если в модели еще нет стандартного продольного или поперечного разреза, предлагаем их автосоздание
            if (!hasSection1)
            {
                result.Add(new SheetCandidateItem
                {
                    VirtualKey = "AUTO_SEC_1",
                    CategoryBadge = "РАЗРЕЗ (АВТО)",
                    ViewName = "Разрез 1–1 (продольный через здание — автопостроение)",
                    BaseGostTitle = "Разрез 1–1",
                    SheetTitlePreview = "Лист АР: Разрез 1–1 (М 1:75) + Составы конструкций и Экспликация полов (ГОСТ 21.501)",
                    ViewType = ViewType.Section,
                    SortGroup = 4,
                    SortElevation = 1,
                    IsCoreArView = true,
                    IsSelected = true,
                    SupportsScale = true,
                    SelectedScale = 75
                });
            }

            if (!hasSection2)
            {
                result.Add(new SheetCandidateItem
                {
                    VirtualKey = "AUTO_SEC_2",
                    CategoryBadge = "РАЗРЕЗ (АВТО)",
                    ViewName = "Разрез 2–2 (поперечный через здание — автопостроение)",
                    BaseGostTitle = "Разрез 2–2",
                    SheetTitlePreview = "Лист АР: Разрез 2–2 (М 1:50) + Составы конструкций и Экспликация полов (ГОСТ 21.501)",
                    ViewType = ViewType.Section,
                    SortGroup = 4,
                    SortElevation = 2,
                    IsCoreArView = true,
                    IsSelected = true,
                    SupportsScale = true,
                    SelectedScale = 50
                });
            }

            // Автоматическое построение 4 фасадов, если в проекте их нет
            if (!hasSouthElevation)
            {
                result.Add(new SheetCandidateItem
                {
                    VirtualKey = "AUTO_ELEV_SOUTH",
                    CategoryBadge = "ФАСАД (АВТО)",
                    ViewName = $"Фасад в осях {nStart}–{nEnd} (южный — автопостроение)",
                    BaseGostTitle = $"Фасад в осях {nStart}–{nEnd}",
                    SheetTitlePreview = $"Лист АР: Фасад в осях {nStart}–{nEnd} (М 1:100) по ГОСТ 21.501-93",
                    ViewType = ViewType.Elevation,
                    SortGroup = 5,
                    SortElevation = 1,
                    IsCoreArView = true,
                    IsSelected = true,
                    SupportsScale = true,
                    SelectedScale = 100
                });
            }

            if (!hasNorthElevation)
            {
                result.Add(new SheetCandidateItem
                {
                    VirtualKey = "AUTO_ELEV_NORTH",
                    CategoryBadge = "ФАСАД (АВТО)",
                    ViewName = $"Фасад в осях {nEnd}–{nStart} (северный — автопостроение)",
                    BaseGostTitle = $"Фасад в осях {nEnd}–{nStart}",
                    SheetTitlePreview = $"Лист АР: Фасад в осях {nEnd}–{nStart} (М 1:100) по ГОСТ 21.501-93",
                    ViewType = ViewType.Elevation,
                    SortGroup = 5,
                    SortElevation = 2,
                    IsCoreArView = true,
                    IsSelected = true,
                    SupportsScale = true,
                    SelectedScale = 100
                });
            }

            if (!hasEastElevation)
            {
                result.Add(new SheetCandidateItem
                {
                    VirtualKey = "AUTO_ELEV_EAST",
                    CategoryBadge = "ФАСАД (АВТО)",
                    ViewName = $"Фасад в осях {lStart}–{lEnd} (восточный — автопостроение)",
                    BaseGostTitle = $"Фасад в осях {lStart}–{lEnd}",
                    SheetTitlePreview = $"Лист АР: Фасад в осях {lStart}–{lEnd} (М 1:100) по ГОСТ 21.501-93",
                    ViewType = ViewType.Elevation,
                    SortGroup = 5,
                    SortElevation = 3,
                    IsCoreArView = true,
                    IsSelected = true,
                    SupportsScale = true,
                    SelectedScale = 100
                });
            }

            if (!hasWestElevation)
            {
                result.Add(new SheetCandidateItem
                {
                    VirtualKey = "AUTO_ELEV_WEST",
                    CategoryBadge = "ФАСАД (АВТО)",
                    ViewName = $"Фасад в осях {lEnd}–{lStart} (западный — автопостроение)",
                    BaseGostTitle = $"Фасад в осях {lEnd}–{lStart}",
                    SheetTitlePreview = $"Лист АР: Фасад в осях {lEnd}–{lStart} (М 1:100) по ГОСТ 21.501-93",
                    ViewType = ViewType.Elevation,
                    SortGroup = 5,
                    SortElevation = 4,
                    IsCoreArView = true,
                    IsSelected = true,
                    SupportsScale = true,
                    SelectedScale = 100
                });
            }

            // 7. Лист 7: Визуализации («РЕНДЕР»)
            result.Add(new SheetCandidateItem
            {
                VirtualKey = "SHEET_RENDER",
                CategoryBadge = "ВИЗУАЛИЗАЦИЯ",
                ViewName = "Лист визуализации (чистый лист А3 с заголовком «РЕНДЕР»)",
                BaseGostTitle = "Визуализация",
                SheetTitlePreview = "Лист АР: Визуализация (чистый лист с надписью «РЕНДЕР»)",
                SortGroup = 99,
                SortElevation = 0,
                IsCoreArView = true,
                IsSelected = true,
                SupportsScale = false,
                SelectedScale = 0
            });

            return result
                .OrderBy(c => c.SortGroup)
                .ThenBy(c => c.SortElevation)
                .ThenBy(c => c.ViewName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool IsSupportedViewType(ViewType vt) => vt switch
        {
            ViewType.FloorPlan => true,
            ViewType.EngineeringPlan => true,
            ViewType.AreaPlan => true,
            ViewType.Section => true,
            ViewType.Elevation => true,
            ViewType.CeilingPlan => true,
            ViewType.ThreeD => true,
            ViewType.Detail => true,
            ViewType.DraftingView => true,
            _ => false
        };

        private static (int SortGroup, string Badge, bool IsCore, bool DefaultSelected) ClassifyViewCandidate(View v)
        {
            string lower = (v.Name ?? string.Empty).ToLowerInvariant();
            bool isSiteOrAnalytical = lower.Contains("площадк") || lower.Contains("site") ||
                                      lower.Contains("аналит") || lower.Contains("analytic") ||
                                      lower.Contains("опорн");

            switch (v.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                    if (isSiteOrAnalytical)
                    {
                        return (7, "ВСПОМОГ. ПЛАН", false, false);
                    }
                    return (2, "ПЛАН ЭТАЖА", true, true);

                case ViewType.Section:
                    return (4, "РАЗРЕЗ", true, true);

                case ViewType.Elevation:
                    return (5, "ФАСАД", true, true);

                case ViewType.CeilingPlan:
                    return (6, "ПЛАН ПОТОЛКА", false, false);

                case ViewType.ThreeD:
                    return (8, "3D ВИД", false, false);

                case ViewType.Detail:
                case ViewType.DraftingView:
                    return (9, "УЗЕЛ / ЧЕРТЕЖ", false, false);

                default:
                    return (10, "ВИД", false, false);
            }
        }

        public static BuildingPurposeCategory ParseBuildingPurposeCategory(string? rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return BuildingPurposeCategory.Residential;
            }

            string lower = rawText.Trim().ToLowerInvariant();
            if (lower.Contains("ресторан") || lower.Contains("кафе") || lower.Contains("общепит") ||
                lower.Contains("столов") || lower.Contains("бистро") || lower.Contains("бар") ||
                lower.Contains("пицц") || lower.Contains("restaurant") || lower.Contains("cafe"))
            {
                return BuildingPurposeCategory.Restaurant;
            }

            if (lower.Contains("торг") || lower.Contains("магазин") || lower.Contains("тц") ||
                lower.Contains("молл") || lower.Contains("супермаркет") || lower.Contains("гипермаркет") ||
                lower.Contains("shop") || lower.Contains("mall"))
            {
                return BuildingPurposeCategory.Retail;
            }

            if (lower.Contains("офис") || lower.Contains("бизнес") || lower.Contains("делов") ||
                lower.Contains("бц") || lower.Contains("контор") || lower.Contains("office"))
            {
                return BuildingPurposeCategory.Office;
            }

            if (lower.Contains("гостиниц") || lower.Contains("отел") || lower.Contains("хостел") ||
                lower.Contains("пансионат") || lower.Contains("hotel"))
            {
                return BuildingPurposeCategory.Hotel;
            }

            if (lower.Contains("сад") || lower.Contains("школ") || lower.Contains("образ") ||
                lower.Contains("детск") || lower.Contains("ясли") || lower.Contains("school"))
            {
                return BuildingPurposeCategory.Education;
            }

            if (lower.Contains("склад") || lower.Contains("производ") || lower.Contains("цех") ||
                lower.Contains("пром") || lower.Contains("логист") || lower.Contains("завод"))
            {
                return BuildingPurposeCategory.Industrial;
            }

            return BuildingPurposeCategory.Residential;
        }

        public static string FormatNameByGost(string? rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                return "Иванов И.И.";
            }

            string cleaned = Regex.Replace(rawInput.Trim(), @"\s+", " ");

            string[] tokens = cleaned
                .Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (tokens.Length == 0)
            {
                return "Иванов И.И.";
            }

            string surname = CapitalizeWord(tokens[0]);

            if (tokens.Length == 1)
            {
                return surname;
            }

            if (tokens.Length == 2)
            {
                if (tokens[1].Length == 2 && tokens[1].All(char.IsLetter) && rawInput.Contains('.'))
                {
                    char i1 = char.ToUpper(tokens[1][0], CultureInfo.CurrentCulture);
                    char i2 = char.ToUpper(tokens[1][1], CultureInfo.CurrentCulture);
                    return $"{surname} {i1}.{i2}.";
                }

                char firstInitial = char.ToUpper(tokens[1][0], CultureInfo.CurrentCulture);
                return $"{surname} {firstInitial}.";
            }

            char init1 = char.ToUpper(tokens[1][0], CultureInfo.CurrentCulture);
            char init2 = char.ToUpper(tokens[2][0], CultureInfo.CurrentCulture);
            return $"{surname} {init1}.{init2}.";
        }

        private static string CapitalizeWord(string word)
        {
            if (string.IsNullOrEmpty(word))
            {
                return word;
            }

            CultureInfo ru = CultureInfo.GetCultureInfo("ru-RU");
            if (word.Length == 1)
            {
                return word.ToUpper(ru);
            }

            string[] parts = word.Split('-');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    parts[i] = char.ToUpper(parts[i][0], ru) + parts[i][1..].ToLower(ru);
                }
            }

            return string.Join("-", parts);
        }

        /// <summary>
        /// Открывает документ семейства основной надписи ("Лист (Форма3)"), находит текстовые метки штампа
        /// ("Фамилия 1", "Должность 1", "Стадия" и т.д.) и прописывает в них отформатированные значения по ГОСТ 21.101-97 / СН РК.
        /// </summary>
        public static void UpdateTitleBlockFamilyStampTexts(
            Document projectDoc,
            Family titleBlockFamily,
            GostStampData stampData)
        {
            if (!titleBlockFamily.IsEditable)
            {
                return;
            }

            Document? familyDoc = null;
            try
            {
                familyDoc = projectDoc.EditFamily(titleBlockFamily);
                if (familyDoc == null)
                {
                    return;
                }

                bool anyModified = false;

                using (Transaction txFam = new Transaction(familyDoc, "Заполнение штампа по ГОСТ РК в семействе"))
                {
                    txFam.Start();

                    List<TextElement> textElements = new FilteredElementCollector(familyDoc)
                        .OfClass(typeof(TextElement))
                        .Cast<TextElement>()
                        .ToList();

                    foreach (TextElement textEl in textElements)
                    {
                        string current = textEl.Text?.Trim() ?? string.Empty;
                        if (string.IsNullOrEmpty(current))
                        {
                            continue;
                        }

                        string? replacement = ResolveGostTextReplacement(current, stampData);
                        if (replacement != null && !string.Equals(current, replacement, StringComparison.Ordinal))
                        {
                            textEl.Text = replacement;
                            anyModified = true;
                        }
                    }

                    FamilyManager famMgr = familyDoc.FamilyManager;
                    if (famMgr != null && famMgr.Types.Size > 0)
                    {
                        foreach (FamilyParameter fp in famMgr.Parameters)
                        {
                            if (fp.StorageType != StorageType.String || fp.IsReadOnly)
                            {
                                continue;
                            }

                            string pName = fp.Definition.Name;
                            string? valToSet = pName switch
                            {
                                var n when n.Contains("Разработал", StringComparison.OrdinalIgnoreCase) => stampData.FormattedDeveloperName,
                                var n when n.Contains("Проверил", StringComparison.OrdinalIgnoreCase) => stampData.FormattedCheckerName,
                                var n when n.Contains("Нормоконтроль", StringComparison.OrdinalIgnoreCase) || n.Contains("Н.контр", StringComparison.OrdinalIgnoreCase) => stampData.FormattedNormControllerName,
                                var n when n.Contains("Утвердил", StringComparison.OrdinalIgnoreCase) || n.Contains("ГИП", StringComparison.OrdinalIgnoreCase) => stampData.FormattedApproverName,
                                var n when n.Contains("Организация", StringComparison.OrdinalIgnoreCase) => stampData.OrganizationName,
                                var n when n.Contains("Стадия", StringComparison.OrdinalIgnoreCase) => stampData.StageCode,
                                _ => null
                            };

                            if (valToSet != null)
                            {
                                try
                                {
                                    famMgr.Set(fp, valToSet);
                                    anyModified = true;
                                }
                                catch
                                {
                                }
                            }
                        }
                    }

                    if (anyModified)
                    {
                        txFam.Commit();
                    }
                    else
                    {
                        txFam.RollBack();
                    }
                }

                if (anyModified)
                {
                    familyDoc.LoadFamily(projectDoc, new GostFamilyLoadOptions());
                }
            }
            catch
            {
            }
            finally
            {
                try
                {
                    familyDoc?.Close(false);
                }
                catch
                {
                }
            }
        }

        private static string? ResolveGostTextReplacement(string rawText, GostStampData stampData)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return null;
            }

            string normalized = Regex.Replace(rawText, @"\s+", " ").Trim();

            // Строка 1: Разработал
            if (normalized.Equals("Должность 1", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("1 должность", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Разраб.", StringComparison.OrdinalIgnoreCase))
            {
                return "Разраб.";
            }

            if (normalized.Equals("Фамилия 1", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("1 фамилия", StringComparison.OrdinalIgnoreCase))
            {
                return stampData.FormattedDeveloperName;
            }

            // Строка 2: Проверил
            if (normalized.Equals("Должность 2", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("2 должность", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Пров.", StringComparison.OrdinalIgnoreCase))
            {
                return "Пров.";
            }

            if (normalized.Equals("Фамилия 2", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("2 фамилия", StringComparison.OrdinalIgnoreCase))
            {
                return stampData.FormattedCheckerName;
            }

            // Строка 3: ГИП / ГАП (по нормам проектирования РК)
            if (normalized.Equals("Должность 3", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("3 должность", StringComparison.OrdinalIgnoreCase))
            {
                return "ГИП";
            }

            if (normalized.Equals("Фамилия 3", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("3 фамилия", StringComparison.OrdinalIgnoreCase))
            {
                return stampData.FormattedApproverName;
            }

            // Строка 4: резервная
            if (normalized.Equals("Должность 4", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("4 должность", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Фамилия 4", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("4 фамилия", StringComparison.OrdinalIgnoreCase))
            {
                return " ";
            }

            // Строка 5: Н.контр. (Нормоконтроль)
            if (normalized.Equals("Должность 5", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("5 должность", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Н.контр.", StringComparison.OrdinalIgnoreCase))
            {
                return "Н.контр.";
            }

            if (normalized.Equals("Фамилия 5", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("5 фамилия", StringComparison.OrdinalIgnoreCase))
            {
                return stampData.FormattedNormControllerName;
            }

            // Строка 6: Утв. / ГИП
            if (normalized.Equals("Должность 6", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("6 должность", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Утв.", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("ГИП", StringComparison.OrdinalIgnoreCase))
            {
                return "Утв.";
            }

            if (normalized.Equals("Фамилия 6", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("6 фамилия", StringComparison.OrdinalIgnoreCase))
            {
                return stampData.FormattedApproverName;
            }

            return null;
        }

        public static void ConfigureTitleBlockInstanceA3(Document doc, ViewSheet sheet)
        {
            Element? tbInstance = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (tbInstance == null) return;

            TrySetIntParameter(tbInstance, "Формат А", 3);
            TrySetIntParameter(tbInstance, "Кратность", 1);
            TrySetIntParameter(tbInstance, "Книжная ориентация", 0);
            TrySetIntParameter(tbInstance, "Альбомная ориентация", 1);
        }

        private static void TrySetIntParameter(Element element, string paramName, int value)
        {
            Parameter? param = element.LookupParameter(paramName);
            if (param != null && !param.IsReadOnly && param.StorageType == StorageType.Integer)
            {
                param.Set(value);
            }
        }

        /// <summary>
        /// Записывает данные по ГОСТ 21.101-97 / СН РК в информацию о проекте, параметры листа (ViewSheet)
        /// и экземпляр основной надписи (TitleBlock).
        /// </summary>
        public static void ApplyGostStampToSheet(
            Document doc,
            ViewSheet sheet,
            GostStampData stampData,
            int? sequentialPageNumber = null,
            int? totalSheetsCount = null)
        {
            string stage = string.IsNullOrWhiteSpace(stampData.StageCode) ? "РП" : stampData.StageCode;

            // 1. Информация о проекте (ProjectInformation)
            ProjectInfo? projInfo = doc.ProjectInformation;
            if (projInfo != null)
            {
                TrySetStringParam(projInfo, BuiltInParameter.PROJECT_AUTHOR, stampData.FormattedDeveloperName);
                TrySetStringParam(projInfo, BuiltInParameter.PROJECT_ORGANIZATION_NAME, stampData.OrganizationName);
                TrySetStringParam(projInfo, BuiltInParameter.PROJECT_BUILDING_NAME, stampData.ProjectName);
                TrySetStringParam(projInfo, BuiltInParameter.PROJECT_NAME, stampData.ProjectName);
                TrySetStringParam(projInfo, BuiltInParameter.PROJECT_NUMBER, stampData.ProjectCode);
                TrySetStringParam(projInfo, BuiltInParameter.PROJECT_ADDRESS, stampData.ProjectAddress);
                TrySetStringParam(projInfo, BuiltInParameter.PROJECT_ISSUE_DATE, stampData.IssueDateGost);
                TrySetStringParam(projInfo, BuiltInParameter.PROJECT_STATUS, stage);

                TrySetNamedStringParam(projInfo, "ADSK_Штамп Наименование объекта", stampData.ProjectName);
                TrySetNamedStringParam(projInfo, "ADSK_Наименование объекта", stampData.ProjectName);
                TrySetNamedStringParam(projInfo, "ADSK_Шифр проекта", stampData.ProjectCode);
                TrySetNamedStringParam(projInfo, "ADSK_Стадия проектирования", stage);
                TrySetNamedStringParam(projInfo, "Стадия", stage);
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 1 Должность", "Разраб.");
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 1 Фамилия", stampData.FormattedDeveloperName);
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 2 Должность", "Пров.");
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 2 Фамилия", stampData.FormattedCheckerName);
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 3 Должность", "ГИП");
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 3 Фамилия", stampData.FormattedApproverName);
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 5 Должность", "Н.контр.");
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 5 Фамилия", stampData.FormattedNormControllerName);
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 6 Должность", "Утв.");
                TrySetNamedStringParam(projInfo, "ADSK_Штамп Строка 6 Фамилия", stampData.FormattedApproverName);
            }

            // 2. Встроенные и пользовательские параметры листа (ViewSheet)
            TrySetStringParam(sheet, BuiltInParameter.SHEET_DESIGNED_BY, stampData.FormattedDeveloperName);
            TrySetStringParam(sheet, BuiltInParameter.SHEET_DRAWN_BY, stampData.FormattedDeveloperName);
            TrySetStringParam(sheet, BuiltInParameter.SHEET_CHECKED_BY, stampData.FormattedCheckerName);
            TrySetStringParam(sheet, BuiltInParameter.SHEET_APPROVED_BY, stampData.FormattedApproverName);
            TrySetStringParam(sheet, BuiltInParameter.SHEET_ISSUE_DATE, stampData.IssueDateGost);

            TrySetNamedStringParam(sheet, "Разработал", stampData.FormattedDeveloperName);
            TrySetNamedStringParam(sheet, "Проверил", stampData.FormattedCheckerName);
            TrySetNamedStringParam(sheet, "ГИП", stampData.FormattedApproverName);
            TrySetNamedStringParam(sheet, "Нормоконтроль", stampData.FormattedNormControllerName);
            TrySetNamedStringParam(sheet, "Утвердил", stampData.FormattedApproverName);
            TrySetNamedStringParam(sheet, "Стадия", stage);
            TrySetNamedStringParam(sheet, "ADSK_Штамп Стадия", stage);
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 1 Должность", "Разраб.");
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 1 Фамилия", stampData.FormattedDeveloperName);
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 2 Должность", "Пров.");
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 2 Фамилия", stampData.FormattedCheckerName);
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 3 Должность", "ГИП");
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 3 Фамилия", stampData.FormattedApproverName);
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 5 Должность", "Н.контр.");
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 5 Фамилия", stampData.FormattedNormControllerName);
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 6 Должность", "Утв.");
            TrySetNamedStringParam(sheet, "ADSK_Штамп Строка 6 Фамилия", stampData.FormattedApproverName);

            if (sequentialPageNumber.HasValue)
            {
                string pageStr = sequentialPageNumber.Value.ToString(CultureInfo.InvariantCulture);
                TrySetNamedStringParam(sheet, "ADSK_Штамп Номер страницы", pageStr);
                TrySetNamedStringParam(sheet, "Номер листа", pageStr);
            }

            if (totalSheetsCount.HasValue && totalSheetsCount.Value > 0)
            {
                string totalStr = totalSheetsCount.Value.ToString(CultureInfo.InvariantCulture);
                TrySetNamedStringParam(sheet, "ADSK_Штамп Количество страниц", totalStr);
                TrySetNamedStringParam(sheet, "Листов", totalStr);
                TrySetNamedIntParam(sheet, "Листов", totalSheetsCount.Value);
            }

            // 3. Экземпляр основной надписи (TitleBlock) на листе
            Element? tbInstance = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (tbInstance != null)
            {
                TrySetNamedIntParam(tbInstance, "Формат А", 3);
                TrySetNamedIntParam(tbInstance, "Кратность", 1);
                TrySetNamedIntParam(tbInstance, "Книжная ориентация", 0);
                TrySetNamedIntParam(tbInstance, "Альбомная ориентация", 1);
                TrySetNamedIntParam(tbInstance, "Нестандартный размер", 0);

                // Включаем отображение дат в заполненных строках штампа
                TrySetNamedIntParam(tbInstance, "Строка 1 Дата", 1);
                TrySetNamedIntParam(tbInstance, "Строка 2 Дата", 1);
                TrySetNamedIntParam(tbInstance, "Строка 3 Дата", 1);
                TrySetNamedIntParam(tbInstance, "Строка 4 Дата", 0);
                TrySetNamedIntParam(tbInstance, "Строка 5 Дата", 1);
                TrySetNamedIntParam(tbInstance, "Строка 6 Дата", 1);

                TrySetNamedStringParam(tbInstance, "Разработал", stampData.FormattedDeveloperName);
                TrySetNamedStringParam(tbInstance, "Проверил", stampData.FormattedCheckerName);
                TrySetNamedStringParam(tbInstance, "ГИП", stampData.FormattedApproverName);
                TrySetNamedStringParam(tbInstance, "Утвердил", stampData.FormattedApproverName);
                TrySetNamedStringParam(tbInstance, "Стадия", stage);
                TrySetNamedStringParam(tbInstance, "Фамилия 1", stampData.FormattedDeveloperName);
                TrySetNamedStringParam(tbInstance, "Фамилия 2", stampData.FormattedCheckerName);
                TrySetNamedStringParam(tbInstance, "Фамилия 3", stampData.FormattedApproverName);
                TrySetNamedStringParam(tbInstance, "Фамилия 5", stampData.FormattedNormControllerName);
                TrySetNamedStringParam(tbInstance, "Фамилия 6", stampData.FormattedApproverName);

                if (totalSheetsCount.HasValue && totalSheetsCount.Value > 0)
                {
                    TrySetNamedStringParam(tbInstance, "Листов", totalSheetsCount.Value.ToString(CultureInfo.InvariantCulture));
                    TrySetNamedIntParam(tbInstance, "Листов", totalSheetsCount.Value);
                }
            }

            // 4. Гарантированно вписываем фамилии и должности прямо в ячейки штампа листа
            // Исключаем листы, которые по требованию пользователя должны быть свободны от штампа подписей (Обложка, Рендер / Визуализация)
            bool isExcludedFromDirectStamp =
                sheet.Name.Contains("Обложка", StringComparison.OrdinalIgnoreCase) ||
                sheet.Name.Contains("Визуализация", StringComparison.OrdinalIgnoreCase) ||
                sheet.Name.Contains("Рендер", StringComparison.OrdinalIgnoreCase);

            if (!isExcludedFromDirectStamp)
            {
                DrawDirectStampTextNotesOnSheet(doc, sheet, stampData);
            }
        }

        private const double MmToFeet = 1.0 / 304.8;

        /// <summary>
        /// Гарантированно вычерчивает текстовые строки фамилий и должностей в ячейки основной надписи (штампа Форма 3)
        /// прямо на листе ViewSheet, даже если семейство TitleBlock не содержит соответствующих параметров.
        /// </summary>
        public static void DrawDirectStampTextNotesOnSheet(
            Document doc,
            ViewSheet sheet,
            GostStampData stampData)
        {
            try
            {
                Element? tbInstance = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();

                if (tbInstance == null) return;

                (double originXFt, double originYFt) = SheetLayoutEngine.GetSheetOriginFt(doc, sheet);

                // Координаты штампа 185х55 мм по ГОСТ 21.101-97 в правом нижнем углу листа А3 (420х297 мм):
                // Правая рамка листа: 5 мм от правого края (X = 415 мм).
                // Ширина штампа: 185 мм -> Левый край штампа: X = 415 - 185 = 230 мм от левого края листа.
                // Нижняя рамка листа: 5 мм от нижнего края (Y = 5 мм).
                double stampLeftX = originXFt + (230.0 * MmToFeet);
                double stampBottomY = originYFt + (5.0 * MmToFeet);

                // Удаляем ранее нанесенные текстовые метки в зоне подписей штампа
                // (по тегу и по координатам для гарантированного исключения наложения слоев текста)
                List<ElementId> oldNotes = new FilteredElementCollector(doc, sheet.Id)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .Where(t =>
                    {
                        string comment = t.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                        if (comment == GostStampComment) return true;
                        XYZ coord = t.Coord;
                        bool inX = coord.X >= stampLeftX - (5.0 * MmToFeet) && coord.X <= stampLeftX + (75.0 * MmToFeet);
                        bool inY = coord.Y >= stampBottomY - (5.0 * MmToFeet) && coord.Y <= stampBottomY + (45.0 * MmToFeet);
                        return inX && inY;
                    })
                    .Select(t => t.Id)
                    .ToList();

                if (oldNotes.Count > 0)
                {
                    try { doc.Delete(oldNotes); } catch { }
                }

                ElementId fontCellId = GetOrCreateGostTextType(doc, 2.5, bold: false, "Штамп_Текст_2.5мм");

                // Столбцы левого блока 65 мм основной надписи (ГОСТ 21.101-97 Форма 3):
                // Графа 10 (Должность): X от 0 до 20 мм -> отступ 2.0 мм
                // Графа 11 (Фамилия):   X от 20 до 40 мм -> отступ 21.0 мм
                // Графа 12 (Подпись):   X от 40 до 55 мм (оставляется пустой под ручную подпись)
                // Графа 13 (Дата):      X от 55 до 65 мм -> отступ 56.0 мм
                double colRoleX = stampLeftX + (2.0 * MmToFeet);
                double colNameX = stampLeftX + (21.0 * MmToFeet);
                double colDateX = stampLeftX + (56.0 * MmToFeet);

                TextNoteOptions cellOpts = new TextNoteOptions(fontCellId)
                {
                    HorizontalAlignment = HorizontalTextAlignment.Left
                };

                // Строки штампа высотой по 5 мм строго ниже строки «Изм. | Кол.уч. ...»:
                // Строка «Изм.»:      Y = 30..35 мм (уже напечатана в семействе)
                // Строка 1 (Разраб.): Y = 25..30 мм -> верх текста 29.3 мм (ровно по центру ячейки)
                // Строка 2 (Пров.):   Y = 20..25 мм -> верх текста 24.3 мм
                // Строка 3 (ГИП):     Y = 15..20 мм -> верх текста 19.3 мм
                // Строка 4 (пусто):   Y = 10..15 мм -> пустая строка (резерв)
                // Строка 5 (Н.контр.):Y = 5..10 мм  -> верх текста 9.3 мм
                // Строка 6 (Утв.):    Y = 0..5 мм   -> верх текста 4.3 мм (не касается нижней линии!)
                (string Role, string Name, double RowTopYOffsetMm)[] stampRows =
                [
                    ("Разраб.", stampData.FormattedDeveloperName, 29.3),
                    ("Пров.",   stampData.FormattedCheckerName,   24.3),
                    ("ГИП",     stampData.FormattedApproverName,  19.3),
                    ("Н.контр.",stampData.FormattedNormControllerName, 9.3),
                    ("Утв.",    stampData.FormattedApproverName,  4.3)
                ];

                foreach (var row in stampRows)
                {
                    double rowY = stampBottomY + (row.RowTopYOffsetMm * MmToFeet);

                    // 1. Должность (графа 10)
                    XYZ pRole = new XYZ(colRoleX, rowY, 0.0);
                    TextNote noteRole = TextNote.Create(doc, sheet.Id, pRole, row.Role, cellOpts);
                    TagStampElement(noteRole);

                    // 2. Фамилия (графа 11) - неразрывный пробел исключает разбиение на 2 строки
                    if (!string.IsNullOrWhiteSpace(row.Name))
                    {
                        string cleanName = row.Name.Trim().Replace(' ', '\u00A0');
                        XYZ pName = new XYZ(colNameX, rowY, 0.0);
                        TextNote noteName = TextNote.Create(doc, sheet.Id, pName, cleanName, cellOpts);
                        TagStampElement(noteName);
                    }

                    // 3. Дата (графа 13)
                    if (!string.IsNullOrWhiteSpace(stampData.IssueDateGost))
                    {
                        XYZ pDate = new XYZ(colDateX, rowY, 0.0);
                        TextNote noteDate = TextNote.Create(doc, sheet.Id, pDate, stampData.IssueDateGost, cellOpts);
                        TagStampElement(noteDate);
                    }
                }
            }
            catch { }
        }

        private static void TagStampElement(Element element)
        {
            try
            {
                Parameter? p = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (p is { IsReadOnly: false })
                {
                    p.Set(GostStampComment);
                }
            }
            catch { }
        }

        private static ElementId GetOrCreateGostTextType(Document doc, double heightMm, bool bold, string suffix)
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
                Parameter? sizeParam = newType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (sizeParam is { IsReadOnly: false })
                {
                    sizeParam.Set(heightMm * MmToFeet);
                }

                Parameter? fontParam = newType.get_Parameter(BuiltInParameter.TEXT_FONT);
                if (fontParam is { IsReadOnly: false })
                {
                    fontParam.Set("ISOCPEUR");
                }

                Parameter? boldParam = newType.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD);
                if (boldParam is { IsReadOnly: false })
                {
                    boldParam.Set(bold ? 1 : 0);
                }

                Parameter? widthParam = newType.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE);
                if (widthParam is { IsReadOnly: false })
                {
                    widthParam.Set(0.85);
                }

                Parameter? bgParam = newType.get_Parameter(BuiltInParameter.TEXT_BACKGROUND);
                if (bgParam is { IsReadOnly: false })
                {
                    bgParam.Set(1); // Transparent
                }

                return newType.Id;
            }
            catch
            {
                return baseType.Id;
            }
        }

        private static void TrySetStringParam(Element element, BuiltInParameter bip, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            Parameter? p = element.get_Parameter(bip);
            if (p is { IsReadOnly: false, StorageType: StorageType.String })
            {
                try
                {
                    p.Set(value);
                }
                catch
                {
                }
            }
        }

        private static void TrySetNamedStringParam(Element element, string paramName, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            Parameter? p = element.LookupParameter(paramName);
            if (p is { IsReadOnly: false, StorageType: StorageType.String })
            {
                try
                {
                    p.Set(value);
                }
                catch
                {
                }
            }
        }

        private static void TrySetNamedIntParam(Element element, string paramName, int value)
        {
            Parameter? p = element.LookupParameter(paramName);
            if (p is { IsReadOnly: false, StorageType: StorageType.Integer })
            {
                try
                {
                    p.Set(value);
                }
                catch
                {
                }
            }
        }

        private sealed class GostFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily,
                bool familyInUse,
                out FamilySource source,
                out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }
    }

    /// <summary>
    /// Диалоговое окно WPF для ввода реквизитов штампа по ГОСТ 21.101-97 / СН РК
    /// и интерактивного выбора видов, из которых будут сформированы листы (с возможностью убрать лишние).
    /// </summary>
    internal sealed class GostAuthorInputWindow : Window
    {
        private readonly TextBox _developerTextBox;
        private readonly TextBox _checkerTextBox;
        private readonly TextBox _gipTextBox;
        private readonly TextBlock _gostPreviewTextBlock;
        private readonly TextBox _organizationTextBox;
        private readonly TextBox _projectNameTextBox;
        private readonly TextBox _projectCodeTextBox;
        private readonly TextBox _projectAddressTextBox;
        private readonly TextBox _studentGroupTextBox;
        private readonly ComboBox _stageComboBox;
        private readonly ComboBox _sitePlanStyleComboBox;
        private readonly ComboBox _boundaryShapeComboBox;
        private readonly ComboBox _buildingPurposeComboBox;
        private readonly ComboBox _sitePlanScaleComboBox;
        private readonly CheckBox _replaceRoomTagsCheckBox;
        private readonly ComboBox _roomTagTypeComboBox;
        private readonly CheckBox _renumberRoomsCheckBox;
        private readonly ComboBox _roomNumberingModeComboBox;

        private readonly Document _doc;
        private readonly List<SheetCandidateItem> _candidates;
        private readonly List<(SheetCandidateItem Item, CheckBox Check, Border RowBorder, ComboBox? ScaleCombo, TextBlock SubPreview)> _rowControls = [];
        private readonly TextBlock? _selectionSummaryText;
        private readonly Button _okButton;
        private readonly TextBox? _filterTextBox;
        private SitePlanEditorConfig? _customSitePlanConfig;
        private bool _suppressCheckEvents;
        private bool _suppressScaleEvents;

        private static readonly (int Value, string Label)[] StandardScaleOptions =
        [
            (10, "М 1:10"),
            (20, "М 1:20"),
            (25, "М 1:25"),
            (40, "М 1:40"),
            (50, "М 1:50"),
            (75, "М 1:75"),
            (100, "М 1:100"),
            (150, "М 1:150"),
            (200, "М 1:200"),
            (250, "М 1:250"),
            (400, "М 1:400"),
            (500, "М 1:500"),
            (1000, "М 1:1000"),
            (2000, "М 1:2000"),
            (5000, "М 1:5000"),
            (0, "Авто (ГОСТ)")
        ];

        public GostStampData? ResultData { get; private set; }

        public GostAuthorInputWindow(
            Document doc,
            string defaultName,
            string defaultChecker,
            string defaultGip,
            string defaultNorm,
            string defaultOrganization,
            string defaultProjectName,
            string defaultProjectCode,
            string defaultStageCode,
            List<SheetCandidateItem> candidates,
            bool showViewSelector,
            string defaultAddress = "г. Алматы, ул. Сатпаева, 22",
            string defaultGroup = "")
        {
            _doc = doc;
            _candidates = candidates;

            bool hasViewSelector = showViewSelector && candidates.Count > 0;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double screenWidth = SystemParameters.PrimaryScreenWidth;

            Title = "Комплект рабочих чертежей по ГОСТ РК / СН РК (Выбор видов и штамп Форма 3)";
            Width = hasViewSelector ? Math.Min(1080, screenWidth * 0.95) : Math.Min(600, screenWidth * 0.95);
            Height = hasViewSelector ? Math.Min(780, screenHeight * 0.90) : Math.Min(720, screenHeight * 0.90);
            MinWidth = hasViewSelector ? 880 : 540;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252));
            FontFamily = new FontFamily("Segoe UI");

            Border rootBorder = new Border
            {
                Padding = new Thickness(20, 16, 20, 16)
            };

            WpfGrid rootGrid = new WpfGrid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            WpfGrid contentColumns = new WpfGrid();
            if (showViewSelector && candidates.Count > 0)
            {
                contentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400, GridUnitType.Pixel) });
                contentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18, GridUnitType.Pixel) });
                contentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                contentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            // =========================================================================
            // ЛЕВАЯ ПАНЕЛЬ: РЕКВИЗИТЫ ШТАМПА ПО ГОСТ 21.101-97 / СН РК И НАСТРОЙКИ ГП
            // =========================================================================
            StackPanel leftStack = new StackPanel();

            TextBlock headerTitle = new TextBlock
            {
                Text = "1. Реквизиты штампа по ГОСТ РК",
                FontSize = 15.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            leftStack.Children.Add(headerTitle);

            TextBlock subHeader = new TextBlock
            {
                Text = "Оформление по ГОСТ 21.101-97, ГОСТ 21.501-93, ГОСТ 21.508-93 и СН РК 1.02-03-2022.",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            leftStack.Children.Add(subHeader);

            // Поля ввода подписей штампа: Разработал, Проверил, ГИП
            WpfGrid namesGrid = new WpfGrid();
            namesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            namesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Pixel) });
            namesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            namesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            namesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            namesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            namesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock devLabel = new TextBlock
            {
                Text = "Разработал (ваше имя):",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(devLabel, 0);
            WpfGrid.SetColumn(devLabel, 0);
            namesGrid.Children.Add(devLabel);

            _developerTextBox = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(defaultName) ? "Турсынов И.Б." : defaultName,
                FontSize = 12.0,
                Padding = new Thickness(6, 4, 6, 4),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 5)
            };
            _developerTextBox.TextChanged += (_, _) => UpdateGostPreview();
            WpfGrid.SetRow(_developerTextBox, 1);
            WpfGrid.SetColumn(_developerTextBox, 0);
            namesGrid.Children.Add(_developerTextBox);

            TextBlock chkLabel = new TextBlock
            {
                Text = "Проверил (проверяющий):",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(chkLabel, 0);
            WpfGrid.SetColumn(chkLabel, 2);
            namesGrid.Children.Add(chkLabel);

            _checkerTextBox = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(defaultChecker) ? "Старков А.А." : defaultChecker,
                FontSize = 12.0,
                Padding = new Thickness(6, 4, 6, 4),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 5)
            };
            _checkerTextBox.TextChanged += (_, _) => UpdateGostPreview();
            WpfGrid.SetRow(_checkerTextBox, 1);
            WpfGrid.SetColumn(_checkerTextBox, 2);
            namesGrid.Children.Add(_checkerTextBox);

            TextBlock gipLabel = new TextBlock
            {
                Text = "ГИП (Главный инженер проекта):",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(gipLabel, 2);
            WpfGrid.SetColumn(gipLabel, 0);
            WpfGrid.SetColumnSpan(gipLabel, 3);
            namesGrid.Children.Add(gipLabel);

            _gipTextBox = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(defaultGip) ? "Турсынов И.Б." : defaultGip,
                FontSize = 12.0,
                Padding = new Thickness(6, 4, 6, 4),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 6)
            };
            _gipTextBox.TextChanged += (_, _) => UpdateGostPreview();
            WpfGrid.SetRow(_gipTextBox, 3);
            WpfGrid.SetColumn(_gipTextBox, 0);
            WpfGrid.SetColumnSpan(_gipTextBox, 3);
            namesGrid.Children.Add(_gipTextBox);

            leftStack.Children.Add(namesGrid);

            Border previewBox = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 242, 254)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(125, 211, 252)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 2, 0, 8)
            };

            _gostPreviewTextBlock = new TextBlock
            {
                FontSize = 11.2,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(3, 105, 161))
            };
            previewBox.Child = _gostPreviewTextBlock;
            leftStack.Children.Add(previewBox);

            WpfGrid extraGrid = new WpfGrid();
            extraGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            extraGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10, GridUnitType.Pixel) });
            extraGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125, GridUnitType.Pixel) });
            for (int r = 0; r < 18; r++)
            {
                extraGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            TextBlock projLabel = new TextBlock
            {
                Text = "Наименование объекта (графа 2):",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(projLabel, 0);
            WpfGrid.SetColumn(projLabel, 0);
            extraGrid.Children.Add(projLabel);

            _projectNameTextBox = new TextBox
            {
                Text = defaultProjectName,
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7)
            };
            WpfGrid.SetRow(_projectNameTextBox, 1);
            WpfGrid.SetColumn(_projectNameTextBox, 0);
            extraGrid.Children.Add(_projectNameTextBox);

            TextBlock codeLabel = new TextBlock
            {
                Text = "Шифр (графа 1):",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(codeLabel, 0);
            WpfGrid.SetColumn(codeLabel, 2);
            extraGrid.Children.Add(codeLabel);

            _projectCodeTextBox = new TextBox
            {
                Text = defaultProjectCode,
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7)
            };
            WpfGrid.SetRow(_projectCodeTextBox, 1);
            WpfGrid.SetColumn(_projectCodeTextBox, 2);
            extraGrid.Children.Add(_projectCodeTextBox);

            TextBlock orgLabel = new TextBlock
            {
                Text = "Проектная организация РК (графа 9):",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(orgLabel, 2);
            WpfGrid.SetColumn(orgLabel, 0);
            extraGrid.Children.Add(orgLabel);

            _organizationTextBox = new TextBox
            {
                Text = defaultOrganization,
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7)
            };
            WpfGrid.SetRow(_organizationTextBox, 3);
            WpfGrid.SetColumn(_organizationTextBox, 0);
            extraGrid.Children.Add(_organizationTextBox);

            TextBlock stageLabel = new TextBlock
            {
                Text = "Стадия (СН РК):",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(stageLabel, 2);
            WpfGrid.SetColumn(stageLabel, 2);
            extraGrid.Children.Add(stageLabel);

            _stageComboBox = new ComboBox
            {
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7)
            };
            _stageComboBox.Items.Add("РП (Рабочий проект)");
            _stageComboBox.Items.Add("П (Проект)");
            _stageComboBox.Items.Add("Р (Рабочая док.)");
            _stageComboBox.Items.Add("ЭП (Эскизный проект)");
            _stageComboBox.SelectedIndex = defaultStageCode switch
            {
                "П" => 1,
                "Р" => 2,
                "ЭП" => 3,
                _ => 0
            };
            _stageComboBox.SelectionChanged += (_, _) => UpdateGostPreview();
            WpfGrid.SetRow(_stageComboBox, 3);
            WpfGrid.SetColumn(_stageComboBox, 2);
            extraGrid.Children.Add(_stageComboBox);

            // Адрес объекта и Группа студента
            TextBlock addrLabel = new TextBlock
            {
                Text = "Адрес объекта (для Ситуационной схемы М 1:5000):",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(addrLabel, 4);
            WpfGrid.SetColumn(addrLabel, 0);
            extraGrid.Children.Add(addrLabel);

            _projectAddressTextBox = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(defaultAddress) ? "г. Алматы, ул. Сатпаева, 22" : defaultAddress,
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7)
            };
            WpfGrid.SetRow(_projectAddressTextBox, 5);
            WpfGrid.SetColumn(_projectAddressTextBox, 0);
            extraGrid.Children.Add(_projectAddressTextBox);

            TextBlock grpLabel = new TextBlock
            {
                Text = "Группа (для Обложки):",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            WpfGrid.SetRow(grpLabel, 4);
            WpfGrid.SetColumn(grpLabel, 2);
            extraGrid.Children.Add(grpLabel);

            _studentGroupTextBox = new TextBox
            {
                Text = defaultGroup ?? "",
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7)
            };
            WpfGrid.SetRow(_studentGroupTextBox, 5);
            WpfGrid.SetColumn(_studentGroupTextBox, 2);
            extraGrid.Children.Add(_studentGroupTextBox);

            TextBlock purposeLabel = new TextBlock
            {
                Text = "Назначение здания (для норм СН РК и зон ГП):",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 2, 0, 2)
            };
            WpfGrid.SetRow(purposeLabel, 6);
            WpfGrid.SetColumn(purposeLabel, 0);
            WpfGrid.SetColumnSpan(purposeLabel, 3);
            extraGrid.Children.Add(purposeLabel);

            _buildingPurposeComboBox = new ComboBox
            {
                FontSize = 12.0,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7),
                IsEditable = true
            };
            _buildingPurposeComboBox.Items.Add("Ресторан / Кафе / Общепит (веранда, дебаркадер, фонтан)");
            _buildingPurposeComboBox.Items.Add("Жилой многоквартирный дом (детская, спорт, ТБО, ковры)");
            _buildingPurposeComboBox.Items.Add("Торговый центр / Супермаркет (парковка, ТБО, дебаркадер)");
            _buildingPurposeComboBox.Items.Add("Бизнес-центр / Офисное здание (патио, аллея, паркинг)");
            _buildingPurposeComboBox.Items.Add("Гостиница / Отель (курортная зона, трансфер, бассейн)");
            _buildingPurposeComboBox.Items.Add("Детский сад / Образовательное учреждение (спортядро)");
            _buildingPurposeComboBox.Items.Add("Складской комплекс / Производство (фуры, КПП, площадки)");
            _buildingPurposeComboBox.SelectedIndex = 0;
            WpfGrid.SetRow(_buildingPurposeComboBox, 7);
            WpfGrid.SetColumn(_buildingPurposeComboBox, 0);
            WpfGrid.SetColumnSpan(_buildingPurposeComboBox, 3);
            extraGrid.Children.Add(_buildingPurposeComboBox);

            TextBlock styleLabel = new TextBlock
            {
                Text = "Дизайн генплана ГП-1:",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 2, 0, 2)
            };
            WpfGrid.SetRow(styleLabel, 8);
            WpfGrid.SetColumn(styleLabel, 0);
            WpfGrid.SetColumnSpan(styleLabel, 3);
            extraGrid.Children.Add(styleLabel);

            _sitePlanStyleComboBox = new ComboBox
            {
                FontSize = 11.5,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7),
                SelectedIndex = 0
            };
            _sitePlanStyleComboBox.Items.Add("🎲 Случайный дизайн (новый уникальный вариант каждый раз)");
            _sitePlanStyleComboBox.Items.Add("🏛️ Представительский (парадная площадь перед фасадом)");
            _sitePlanStyleComboBox.Items.Add("🚗 Деловой (расширенная парковка и круговой проезд)");
            _sitePlanStyleComboBox.Items.Add("🌳 Парковый кампус (максимум озеленения и аллей)");
            _sitePlanStyleComboBox.Items.Add("📐 Угловой хаб (компактный въезд у угла и сквер)");
            WpfGrid.SetRow(_sitePlanStyleComboBox, 9);
            WpfGrid.SetColumn(_sitePlanStyleComboBox, 0);
            WpfGrid.SetColumnSpan(_sitePlanStyleComboBox, 3);
            extraGrid.Children.Add(_sitePlanStyleComboBox);

            TextBlock shapeLabel = new TextBlock
            {
                Text = "Контур границы участка (красная линия по ГОСТ 21.508):",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 2, 0, 2)
            };
            WpfGrid.SetRow(shapeLabel, 10);
            WpfGrid.SetColumn(shapeLabel, 0);
            WpfGrid.SetColumnSpan(shapeLabel, 3);
            extraGrid.Children.Add(shapeLabel);

            _boundaryShapeComboBox = new ComboBox
            {
                FontSize = 11.5,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 7),
                SelectedIndex = 0
            };
            _boundaryShapeComboBox.Items.Add("📐 Угловой участок со скосом улиц (7 вершин)");
            _boundaryShapeComboBox.Items.Add("📐 Трапециевидный участок (6 вершин)");
            _boundaryShapeComboBox.Items.Add("📐 Сложный многоугольный (8 вершин)");
            _boundaryShapeComboBox.Items.Add("📐 Прямоугольный классический (4 вершины)");
            _boundaryShapeComboBox.Items.Add("🎲 Случайная форма (процедурный контур)");
            WpfGrid.SetRow(_boundaryShapeComboBox, 11);
            WpfGrid.SetColumn(_boundaryShapeComboBox, 0);
            WpfGrid.SetColumnSpan(_boundaryShapeComboBox, 3);
            extraGrid.Children.Add(_boundaryShapeComboBox);

            TextBlock gpScaleLabel = new TextBlock
            {
                Text = "Масштаб генплана ГП-1 (ГОСТ 21.508-93):",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 2, 0, 2)
            };
            WpfGrid.SetRow(gpScaleLabel, 12);
            WpfGrid.SetColumn(gpScaleLabel, 0);
            WpfGrid.SetColumnSpan(gpScaleLabel, 3);
            extraGrid.Children.Add(gpScaleLabel);

            _sitePlanScaleComboBox = CreateScaleComboBox(defaultScale: 500, width: double.NaN);
            _sitePlanScaleComboBox.SelectionChanged += (_, _) =>
            {
                if (_suppressScaleEvents) return;
                int chosenScale = GetSelectedScaleFromCombo(_sitePlanScaleComboBox, 500);
                ApplyCategoryScale("GP", chosenScale);
            };
            WpfGrid.SetRow(_sitePlanScaleComboBox, 13);
            WpfGrid.SetColumn(_sitePlanScaleComboBox, 0);
            WpfGrid.SetColumnSpan(_sitePlanScaleComboBox, 3);
            extraGrid.Children.Add(_sitePlanScaleComboBox);

            _replaceRoomTagsCheckBox = new CheckBox
            {
                Content = "🏷️ Заменять марки помещений на ADSK (по ГОСТ)",
                IsChecked = true,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 6, 0, 3)
            };
            WpfGrid.SetRow(_replaceRoomTagsCheckBox, 14);
            WpfGrid.SetColumn(_replaceRoomTagsCheckBox, 0);
            WpfGrid.SetColumnSpan(_replaceRoomTagsCheckBox, 3);
            extraGrid.Children.Add(_replaceRoomTagsCheckBox);

            _roomTagTypeComboBox = new ComboBox
            {
                FontSize = 11.2,
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 0, 0, 4)
            };
            foreach (string t in RoomTagManager.StandardAdskTypes)
            {
                _roomTagTypeComboBox.Items.Add(t);
            }
            _roomTagTypeComboBox.SelectedIndex = 0; // Номер Имя
            _replaceRoomTagsCheckBox.Checked += (_, _) => _roomTagTypeComboBox.IsEnabled = true;
            _replaceRoomTagsCheckBox.Unchecked += (_, _) => _roomTagTypeComboBox.IsEnabled = false;

            WpfGrid.SetRow(_roomTagTypeComboBox, 15);
            WpfGrid.SetColumn(_roomTagTypeComboBox, 0);
            WpfGrid.SetColumnSpan(_roomTagTypeComboBox, 3);
            extraGrid.Children.Add(_roomTagTypeComboBox);

            _renumberRoomsCheckBox = new CheckBox
            {
                Content = "🔢 Упорядочить нумерацию помещений по ГОСТ (1, 2, 3...)",
                IsChecked = true,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 6, 0, 3)
            };
            WpfGrid.SetRow(_renumberRoomsCheckBox, 16);
            WpfGrid.SetColumn(_renumberRoomsCheckBox, 0);
            WpfGrid.SetColumnSpan(_renumberRoomsCheckBox, 3);
            extraGrid.Children.Add(_renumberRoomsCheckBox);

            _roomNumberingModeComboBox = new ComboBox
            {
                FontSize = 11.2,
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 0, 0, 4)
            };
            foreach (string label in RoomRenumberEngine.NumberingModeLabels)
            {
                _roomNumberingModeComboBox.Items.Add(label);
            }
            _roomNumberingModeComboBox.SelectedIndex = 0; // По этажам (1, 2, 3...)
            _renumberRoomsCheckBox.Checked += (_, _) => _roomNumberingModeComboBox.IsEnabled = true;
            _renumberRoomsCheckBox.Unchecked += (_, _) => _roomNumberingModeComboBox.IsEnabled = false;

            WpfGrid.SetRow(_roomNumberingModeComboBox, 17);
            WpfGrid.SetColumn(_roomNumberingModeComboBox, 0);
            WpfGrid.SetColumnSpan(_roomNumberingModeComboBox, 3);
            extraGrid.Children.Add(_roomNumberingModeComboBox);

            leftStack.Children.Add(extraGrid);

            // Кнопка запуска интерактивного графического редактора ген-плана прямо из этого окна
            Button openSiteEditorBtn = new Button
            {
                Content = "🗺️ Открыть подробный редактор ген-плана (контур, асфальт, текстуры слоев)...",
                Height = 36,
                Margin = new Thickness(0, 8, 0, 4),
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.5,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            openSiteEditorBtn.Click += (_, _) =>
            {
                try
                {
                    (CurveLoop bLoop, BoundingBoxXYZ bBox) = GenerateDetailedSitePlanCommand.ExtractBuildingFootprint(_doc);
                    GostStampData currentStamp = GostStampHelper.GetDefaultGostStampData(_doc);
                    SitePlanEditorWindow editor = new SitePlanEditorWindow(bLoop, bBox, currentStamp)
                    {
                        Owner = this
                    };
                    if (editor.ShowDialog() == true && editor.ResultConfig != null)
                    {
                        _customSitePlanConfig = editor.ResultConfig;
                        openSiteEditorBtn.Content = $"✅ Ген-план настроен вручную (S уч. = {_customSitePlanConfig.TotalSiteAreaM2:F0} м², асфальт = {_customSitePlanConfig.AsphaltAreaM2:F0} м²) — Изменить...";
                        openSiteEditorBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105));
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Не удалось открыть редактор ген-плана:\n{ex.Message}", "Редактор ген-плана", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            leftStack.Children.Add(openSiteEditorBtn);

            ScrollViewer leftScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 8, 0),
                Focusable = false
            };
            leftScrollViewer.Content = leftStack;
            leftScrollViewer.PreviewMouseWheel += (sender, e) =>
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

            WpfGrid.SetColumn(leftScrollViewer, 0);
            contentColumns.Children.Add(leftScrollViewer);

            // =========================================================================
            // ПРАВАЯ ПАНЕЛЬ: ИНТЕРАКТИВНЫЙ СПИСОК ВИДОВ И СОЗДАВАЕМЫХ ЛИСТОВ С ГАЛОЧКАМИ
            // =========================================================================
            _okButton = new Button
            {
                Content = "Записать по ГОСТ РК и создать листы",
                MinWidth = 260,
                Padding = new Thickness(16, 0, 16, 0),
                Height = 34,
                IsDefault = true,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(2, 132, 199)),
                BorderThickness = new Thickness(0)
            };
            _okButton.Click += OnAcceptClicked;

            if (showViewSelector && candidates.Count > 0)
            {
                WpfGrid rightGrid = new WpfGrid();
                rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                // Заголовок правой панели
                StackPanel rightHeader = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                TextBlock rightTitle = new TextBlock
                {
                    Text = "2. Выбор видов и масштабов чертежей (включая Генплан ГП-1)",
                    FontSize = 15.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42))
                };
                rightHeader.Children.Add(rightTitle);

                _selectionSummaryText = new TextBlock
                {
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                rightHeader.Children.Add(_selectionSummaryText);
                WpfGrid.SetRow(rightHeader, 0);
                rightGrid.Children.Add(rightHeader);

                // Кнопки быстрых пресетов выбора
                WrapPanel presetPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

                Button btnCoreOnly = CreateSmallActionButton("⭐ Основные по ГОСТ РК (ГП + АР)", isPrimary: true);
                btnCoreOnly.Click += (_, _) => ApplySelectionPreset(mode: "CORE");

                Button btnSelectAll = CreateSmallActionButton("☑ Выбрать все", isPrimary: false);
                btnSelectAll.Click += (_, _) => ApplySelectionPreset(mode: "ALL");

                Button btnClearAll = CreateSmallActionButton("☐ Снять все", isPrimary: false);
                btnClearAll.Click += (_, _) => ApplySelectionPreset(mode: "NONE");

                Button btnPlansSections = CreateSmallActionButton("📐 Только Планы + Разрезы", isPrimary: false);
                btnPlansSections.Click += (_, _) => ApplySelectionPreset(mode: "PLANS_SECTIONS");

                presetPanel.Children.Add(btnCoreOnly);
                presetPanel.Children.Add(btnSelectAll);
                presetPanel.Children.Add(btnClearAll);
                presetPanel.Children.Add(btnPlansSections);

                WpfGrid.SetRow(presetPanel, 1);
                rightGrid.Children.Add(presetPanel);

                // Панель быстрого назначения масштабов по типам чертежей (Генплан, Планы, Разрезы, Фасады)
                Border groupScaleBorder = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                WrapPanel groupScalePanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                groupScalePanel.Children.Add(new TextBlock
                {
                    Text = "📏 Быстрый масштаб по группам:",
                    FontSize = 11.2,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 2)
                });

                groupScalePanel.Children.Add(CreateGroupScaleSelector("ГП:", "GP", 500));
                groupScalePanel.Children.Add(CreateGroupScaleSelector("Планы:", "PLANS", 100));
                groupScalePanel.Children.Add(CreateGroupScaleSelector("Разрезы:", "SECTIONS", 75));
                groupScalePanel.Children.Add(CreateGroupScaleSelector("Фасады:", "FACADES", 100));

                groupScaleBorder.Child = groupScalePanel;
                WpfGrid.SetRow(groupScaleBorder, 2);
                rightGrid.Children.Add(groupScaleBorder);

                // Поле быстрого поиска/фильтра видов
                WpfGrid filterBar = new WpfGrid { Margin = new Thickness(0, 0, 0, 6) };
                filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                filterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                TextBlock filterLabel = new TextBlock
                {
                    Text = "🔍 Поиск вида:",
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                    Margin = new Thickness(0, 0, 6, 0)
                };
                WpfGrid.SetColumn(filterLabel, 0);
                filterBar.Children.Add(filterLabel);

                _filterTextBox = new TextBox
                {
                    FontSize = 12,
                    Padding = new Thickness(6, 3, 6, 3),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(1)
                };
                _filterTextBox.TextChanged += (_, _) => ApplyFilter();
                WpfGrid.SetColumn(_filterTextBox, 1);
                filterBar.Children.Add(_filterTextBox);

                WpfGrid.SetRow(filterBar, 3);
                rightGrid.Children.Add(filterBar);

                // Прокручиваемый список карточек видов
                Border listContainer = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(6)
                };

                ScrollViewer scrollViewer = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };

                StackPanel itemsStack = new StackPanel();
                foreach (SheetCandidateItem item in _candidates)
                {
                    Border rowBorder = CreateCandidateRowControl(item);
                    itemsStack.Children.Add(rowBorder);
                }

                scrollViewer.Content = itemsStack;
                listContainer.Child = scrollViewer;

                WpfGrid.SetRow(listContainer, 4);
                rightGrid.Children.Add(listContainer);

                WpfGrid.SetColumn(rightGrid, 2);
                contentColumns.Children.Add(rightGrid);
            }

            WpfGrid.SetRow(contentColumns, 0);
            rootGrid.Children.Add(contentColumns);

            // =========================================================================
            // НИЖНЯЯ ПАНЕЛЬ КНОПОК
            // =========================================================================
            StackPanel buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            Button cancelButton = new Button
            {
                Content = "Отмена",
                Width = 105,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                IsCancel = true,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225))
            };
            cancelButton.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };

            buttonPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(_okButton);

            WpfGrid.SetRow(buttonPanel, 1);
            rootGrid.Children.Add(buttonPanel);

            rootBorder.Child = rootGrid;
            Content = rootBorder;

            UpdateGostPreview();
            UpdateSelectionSummary();

            // Синхронизируем масштабы по группам для всех строк после инициализации
            ApplyCategoryScale("PLANS", 100);
            ApplyCategoryScale("SECTIONS", 75);
            ApplyCategoryScale("FACADES", 100);

            Loaded += (_, _) =>
            {
                _developerTextBox.Focus();
                _developerTextBox.SelectAll();
            };
        }

        private static Button CreateSmallActionButton(string text, bool isPrimary)
        {
            return new Button
            {
                Content = text,
                FontSize = 11.2,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = Cursors.Hand,
                Background = isPrimary
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 242, 254))
                    : Brushes.White,
                Foreground = isPrimary
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(3, 105, 161))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
                BorderBrush = isPrimary
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(125, 211, 252))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1)
            };
        }

        private static ComboBox CreateScaleComboBox(int defaultScale, double width)
        {
            ComboBox combo = new ComboBox
            {
                FontSize = 11.2,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(5, 2, 5, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            if (!double.IsNaN(width))
            {
                combo.Width = width;
            }

            int selectedIdx = 0;
            for (int i = 0; i < StandardScaleOptions.Length; i++)
            {
                (int val, string label) = StandardScaleOptions[i];
                combo.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag = val
                });
                if (val == defaultScale)
                {
                    selectedIdx = i;
                }
            }

            combo.SelectedIndex = selectedIdx;
            return combo;
        }

        private static int GetSelectedScaleFromCombo(ComboBox combo, int fallback)
        {
            int idx = combo.SelectedIndex;
            if (idx >= 0 && idx < StandardScaleOptions.Length)
            {
                return StandardScaleOptions[idx].Value;
            }
            if (combo.SelectedItem is ComboBoxItem cbi && cbi.Tag is int val)
            {
                return val;
            }
            return fallback;
        }

        private static void SetComboScaleValue(ComboBox combo, int scaleValue)
        {
            for (int i = 0; i < StandardScaleOptions.Length; i++)
            {
                if (StandardScaleOptions[i].Value == scaleValue)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private StackPanel CreateGroupScaleSelector(string labelText, string categoryKey, int initialScale)
        {
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 2)
            };

            sp.Children.Add(new TextBlock
            {
                Text = labelText,
                FontSize = 11.0,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            });

            ComboBox cb = CreateScaleComboBox(initialScale, width: 88);
            cb.SelectionChanged += (_, _) =>
            {
                if (_suppressScaleEvents) return;
                int chosenScale = GetSelectedScaleFromCombo(cb, initialScale);
                ApplyCategoryScale(categoryKey, chosenScale);
            };
            sp.Children.Add(cb);
            return sp;
        }

        private void ApplyCategoryScale(string categoryKey, int newScale)
        {
            _suppressScaleEvents = true;
            try
            {
                if (categoryKey == "GP")
                {
                    SetComboScaleValue(_sitePlanScaleComboBox, newScale);
                }

                foreach ((SheetCandidateItem item, _, _, ComboBox? scaleCombo, TextBlock subPreview) in _rowControls)
                {
                    if (!item.SupportsScale || scaleCombo == null)
                    {
                        continue;
                    }

                    bool match = categoryKey switch
                    {
                        "GP" => item.VirtualKey == "SHEET_SITE_PLAN",
                        "PLANS" => (item.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan ||
                                   (item.CategoryBadge != null && item.CategoryBadge.IndexOf("ПЛАН", StringComparison.OrdinalIgnoreCase) >= 0)) &&
                                   item.VirtualKey != "SHEET_SITE_PLAN",
                        "SECTIONS" => item.ViewType == ViewType.Section || item.VirtualKey is "AUTO_SEC_1" or "AUTO_SEC_2",
                        "FACADES" => item.ViewType == ViewType.Elevation || item.VirtualKey.StartsWith("AUTO_ELEV_"),
                        _ => false
                    };

                    if (match)
                    {
                        item.SelectedScale = newScale;
                        SetComboScaleValue(scaleCombo, newScale);
                        subPreview.Text = FormatRowPreviewText(item);
                    }
                }
            }
            finally
            {
                _suppressScaleEvents = false;
            }
        }

        private static string FormatRowPreviewText(SheetCandidateItem item)
        {
            if (!item.SupportsScale)
            {
                return item.SheetTitlePreview;
            }

            string scaleLabel = item.SelectedScale > 0
                ? $"М 1:{item.SelectedScale}"
                : "Авто по ГОСТ";

            if (item.VirtualKey == "SHEET_SITE_PLAN")
            {
                return $"Лист ГП-1: Генеральный план участка ({scaleLabel}) + экспликация + нормативный расчёт СН РК + роза ветров";
            }

            if (item.VirtualKey is "AUTO_SEC_1" or "AUTO_SEC_2")
            {
                return $"На лист: {item.BaseGostTitle} ({scaleLabel}) + высотные отметки + пирог кровли и полов";
            }

            string baseTitle = string.IsNullOrWhiteSpace(item.BaseGostTitle) ? item.ViewName : item.BaseGostTitle;
            return $"На лист: {baseTitle} ({scaleLabel}) + ведомость/спецификация по ГОСТ РК";
        }

        private static bool IsInsideInteractiveControl(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is CheckBox or ComboBox or ComboBoxItem or System.Windows.Controls.Primitives.Popup)
                {
                    return true;
                }
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private Border CreateCandidateRowControl(SheetCandidateItem item)
        {
            Border rowBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand
            };

            WpfGrid rowGrid = new WpfGrid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            CheckBox cb = new CheckBox
            {
                IsChecked = item.IsSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            (System.Windows.Media.Color badgeBg, System.Windows.Media.Color badgeFg) = GetBadgeColors(item.CategoryBadge);
            Border badgeBorder = new Border
            {
                Background = new SolidColorBrush(badgeBg),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.CategoryBadge,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(badgeFg)
                }
            };

            StackPanel textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock mainTitle = new TextBlock
            {
                Text = item.ViewName,
                FontSize = 12.2,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            TextBlock subPreview = new TextBlock
            {
                Text = FormatRowPreviewText(item),
                FontSize = 10.8,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            textStack.Children.Add(mainTitle);
            textStack.Children.Add(subPreview);

            WpfGrid.SetColumn(cb, 0);
            WpfGrid.SetColumn(badgeBorder, 1);
            WpfGrid.SetColumn(textStack, 2);

            rowGrid.Children.Add(cb);
            rowGrid.Children.Add(badgeBorder);
            rowGrid.Children.Add(textStack);

            if (!string.IsNullOrEmpty(item.ExistingSheetBadge))
            {
                TextBlock statusBlock = new TextBlock
                {
                    Text = item.ExistingSheetBadge,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 148, 136)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0)
                };
                WpfGrid.SetColumn(statusBlock, 3);
                rowGrid.Children.Add(statusBlock);
            }

            ComboBox? scaleCombo = null;
            if (item.SupportsScale)
            {
                scaleCombo = CreateScaleComboBox(item.SelectedScale, width: 96);
                scaleCombo.Margin = new Thickness(6, 0, 0, 0);
                scaleCombo.ToolTip = "Выберите масштаб чертежа на листе по ГОСТ (или Авто)";
                scaleCombo.SelectionChanged += (_, _) =>
                {
                    if (_suppressScaleEvents) return;
                    item.SelectedScale = GetSelectedScaleFromCombo(scaleCombo, item.SelectedScale);
                    subPreview.Text = FormatRowPreviewText(item);

                    if (item.VirtualKey == "SHEET_SITE_PLAN")
                    {
                        _suppressScaleEvents = true;
                        try
                        {
                            SetComboScaleValue(_sitePlanScaleComboBox, item.SelectedScale);
                        }
                        finally
                        {
                            _suppressScaleEvents = false;
                        }
                    }
                };
                WpfGrid.SetColumn(scaleCombo, 4);
                rowGrid.Children.Add(scaleCombo);
            }

            rowBorder.Child = rowGrid;

            cb.Checked += (_, _) =>
            {
                if (_suppressCheckEvents) return;
                item.IsSelected = true;
                UpdateRowVisualStyle(rowBorder, true);
                UpdateSelectionSummary();
            };

            cb.Unchecked += (_, _) =>
            {
                if (_suppressCheckEvents) return;
                item.IsSelected = false;
                UpdateRowVisualStyle(rowBorder, false);
                UpdateSelectionSummary();
            };

            rowBorder.MouseLeftButtonDown += (_, e) =>
            {
                if (!IsInsideInteractiveControl(e.OriginalSource as DependencyObject))
                {
                    cb.IsChecked = !(cb.IsChecked ?? false);
                    e.Handled = true;
                }
            };

            UpdateRowVisualStyle(rowBorder, item.IsSelected);
            _rowControls.Add((item, cb, rowBorder, scaleCombo, subPreview));

            return rowBorder;
        }

        private static void UpdateRowVisualStyle(Border rowBorder, bool isSelected)
        {
            if (isSelected)
            {
                rowBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 249, 255));
                rowBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(125, 211, 252));
                rowBorder.Opacity = 1.0;
            }
            else
            {
                rowBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252));
                rowBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240));
                rowBorder.Opacity = 0.65;
            }
        }

        private static (System.Windows.Media.Color Bg, System.Windows.Media.Color Fg) GetBadgeColors(string badge)
        {
            if (badge.Contains("ГП", StringComparison.OrdinalIgnoreCase))
                return (System.Windows.Media.Color.FromRgb(220, 252, 231), System.Windows.Media.Color.FromRgb(21, 128, 61));
            if (badge.Contains("ОБЩИЕ", StringComparison.OrdinalIgnoreCase))
                return (System.Windows.Media.Color.FromRgb(219, 234, 254), System.Windows.Media.Color.FromRgb(29, 78, 216));
            if (badge.Contains("ПЛАН ЭТАЖА", StringComparison.OrdinalIgnoreCase))
                return (System.Windows.Media.Color.FromRgb(224, 242, 254), System.Windows.Media.Color.FromRgb(3, 105, 161));
            if (badge.Contains("РАЗРЕЗ", StringComparison.OrdinalIgnoreCase))
                return (System.Windows.Media.Color.FromRgb(254, 243, 199), System.Windows.Media.Color.FromRgb(180, 83, 9));
            if (badge.Contains("ФАСАД", StringComparison.OrdinalIgnoreCase))
                return (System.Windows.Media.Color.FromRgb(243, 232, 255), System.Windows.Media.Color.FromRgb(126, 34, 206));
            return (System.Windows.Media.Color.FromRgb(226, 232, 240), System.Windows.Media.Color.FromRgb(71, 85, 105));
        }

        private void ApplySelectionPreset(string mode)
        {
            _suppressCheckEvents = true;
            try
            {
                foreach ((SheetCandidateItem item, CheckBox cb, Border rowBorder, _, _) in _rowControls)
                {
                    bool select = mode switch
                    {
                        "ALL" => true,
                        "NONE" => false,
                        "CORE" => item.IsCoreArView,
                        "PLANS_SECTIONS" => item.VirtualKey == "SHEET_GENERAL_DATA" ||
                                            item.ViewType is ViewType.FloorPlan or ViewType.Section,
                        _ => item.IsSelected
                    };

                    item.IsSelected = select;
                    cb.IsChecked = select;
                    UpdateRowVisualStyle(rowBorder, select);
                }
            }
            finally
            {
                _suppressCheckEvents = false;
            }

            UpdateSelectionSummary();
        }

        private void ApplyFilter()
        {
            string q = _filterTextBox?.Text?.Trim() ?? string.Empty;
            foreach ((SheetCandidateItem item, _, Border rowBorder, _, _) in _rowControls)
            {
                if (string.IsNullOrEmpty(q) ||
                    item.ViewName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    item.CategoryBadge.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    item.SheetTitlePreview.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    rowBorder.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    rowBorder.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
        }

        private void UpdateSelectionSummary()
        {
            if (_candidates.Count == 0)
            {
                return;
            }

            int selectedCount = _candidates.Count(c => c.IsSelected);
            int totalCount = _candidates.Count;

            if (_selectionSummaryText != null)
            {
                _selectionSummaryText.Text =
                    $"Выбрано листов к созданию: {selectedCount} из {totalCount} (для каждого вида справа можно выбрать масштаб М 1:...)";
            }

            _okButton.Content = $"Создать выбранные листы по ГОСТ РК ({selectedCount} шт.)";
        }

        private string GetSelectedStageCode()
        {
            return _stageComboBox.SelectedIndex switch
            {
                1 => "П",
                2 => "Р",
                3 => "ЭП",
                _ => "РП"
            };
        }

        private void UpdateGostPreview()
        {
            string dev = GostStampHelper.FormatNameByGost(_developerTextBox.Text);
            string chk = GostStampHelper.FormatNameByGost(_checkerTextBox.Text);
            string gip = GostStampHelper.FormatNameByGost(_gipTextBox.Text);
            string dateGost = DateTime.Now.ToString("MM.yy", CultureInfo.InvariantCulture);
            string stage = GetSelectedStageCode();
            _gostPreviewTextBlock.Text =
                $"Штамп ГОСТ РК:   Разраб.: {dev}   |   Пров.: {chk}   |   ГИП: {gip}\nСтадия: {stage}   |   Дата: {dateGost}";
        }

        private void OnAcceptClicked(object sender, RoutedEventArgs e)
        {
            string rawDev = _developerTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawDev))
            {
                MessageBox.Show(
                    this,
                    "Пожалуйста, введите ваше имя (Разработал) для заполнения основной надписи по ГОСТ.",
                    "Заполнение штампа по ГОСТ РК",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _developerTextBox.Focus();
                return;
            }

            string rawChk = _checkerTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawChk))
            {
                rawChk = "Петров П.П.";
            }

            string rawGip = _gipTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawGip))
            {
                rawGip = rawDev;
            }

            if (_candidates.Count > 0 && _candidates.All(c => !c.IsSelected))
            {
                MessageBox.Show(
                    this,
                    "Не выбран ни один вид или лист. Отметьте галочками хотя бы один лист для создания.",
                    "Выбор видов для листов",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string formattedDev = GostStampHelper.FormatNameByGost(rawDev);
            string formattedChk = GostStampHelper.FormatNameByGost(rawChk);
            string formattedGip = GostStampHelper.FormatNameByGost(rawGip);
            string dateGost = DateTime.Now.ToString("MM.yy", CultureInfo.InvariantCulture);
            string stageCode = GetSelectedStageCode();

            string org = string.IsNullOrWhiteSpace(_organizationTextBox.Text)
                ? "ТОО «Казгорпроект»"
                : _organizationTextBox.Text.Trim();

            string proj = string.IsNullOrWhiteSpace(_projectNameTextBox.Text)
                ? "Здание"
                : _projectNameTextBox.Text.Trim();

            string code = string.IsNullOrWhiteSpace(_projectCodeTextBox.Text)
                ? "2026-АР"
                : _projectCodeTextBox.Text.Trim();

            SitePlanStyle chosenStyle = _sitePlanStyleComboBox.SelectedIndex switch
            {
                1 => SitePlanStyle.FrontPlaza,
                2 => SitePlanStyle.BusinessHub,
                3 => SitePlanStyle.GreenCampus,
                4 => SitePlanStyle.CornerHub,
                _ => SitePlanStyle.RandomProcedural
            };

            SiteBoundaryShape chosenShape = _boundaryShapeComboBox.SelectedIndex switch
            {
                0 => SiteBoundaryShape.CornerLot,
                1 => SiteBoundaryShape.Trapezoidal,
                2 => SiteBoundaryShape.Polygonal,
                3 => SiteBoundaryShape.Rectangular,
                _ => SiteBoundaryShape.Random
            };

            string rawPurpose = string.IsNullOrWhiteSpace(_buildingPurposeComboBox.Text)
                ? "Ресторан"
                : _buildingPurposeComboBox.Text.Trim();
            BuildingPurposeCategory purposeCategory = GostStampHelper.ParseBuildingPurposeCategory(rawPurpose);

            bool includeSitePlan = _candidates.Count == 0 ||
                                   _candidates.Any(c => c.VirtualKey == "SHEET_SITE_PLAN" && c.IsSelected);
            bool includeGeneralData = _candidates.Count == 0 ||
                                      _candidates.Any(c => c.VirtualKey == "SHEET_GENERAL_DATA" && c.IsSelected);
            bool createAutoSec1 = _candidates.Count == 0 ||
                                  _candidates.Any(c => c.VirtualKey == "AUTO_SEC_1" && c.IsSelected);
            bool createAutoSec2 = _candidates.Count == 0 ||
                                  _candidates.Any(c => c.VirtualKey == "AUTO_SEC_2" && c.IsSelected);
            bool createAutoElevSouth = _candidates.Count == 0 ||
                                      _candidates.Any(c => c.VirtualKey == "AUTO_ELEV_SOUTH" && c.IsSelected);
            bool createAutoElevNorth = _candidates.Count == 0 ||
                                      _candidates.Any(c => c.VirtualKey == "AUTO_ELEV_NORTH" && c.IsSelected);
            bool createAutoElevEast = _candidates.Count == 0 ||
                                     _candidates.Any(c => c.VirtualKey == "AUTO_ELEV_EAST" && c.IsSelected);
            bool createAutoElevWest = _candidates.Count == 0 ||
                                     _candidates.Any(c => c.VirtualKey == "AUTO_ELEV_WEST" && c.IsSelected);

            // 1. Принудительно считываем выбранные значения масштабов напрямую из UI ComboBox
            foreach ((SheetCandidateItem item, _, _, ComboBox? scaleCombo, _) in _rowControls)
            {
                if (item.SupportsScale && scaleCombo != null)
                {
                    item.SelectedScale = GetSelectedScaleFromCombo(scaleCombo, item.SelectedScale);
                }
            }

            int sitePlanScale = GetSelectedScaleFromCombo(_sitePlanScaleComboBox, 500);
            SheetCandidateItem? gpCandidate = _candidates.FirstOrDefault(c => c.VirtualKey == "SHEET_SITE_PLAN");
            if (gpCandidate is { SelectedScale: > 0 })
            {
                sitePlanScale = gpCandidate.SelectedScale;
            }

            int autoSec1Scale = _candidates.FirstOrDefault(c => c.VirtualKey == "AUTO_SEC_1")?.SelectedScale ?? 75;
            int autoSec2Scale = _candidates.FirstOrDefault(c => c.VirtualKey == "AUTO_SEC_2")?.SelectedScale ?? 50;
            int autoElevSouthScale = _candidates.FirstOrDefault(c => c.VirtualKey == "AUTO_ELEV_SOUTH")?.SelectedScale ?? 100;
            int autoElevNorthScale = _candidates.FirstOrDefault(c => c.VirtualKey == "AUTO_ELEV_NORTH")?.SelectedScale ?? 100;
            int autoElevEastScale = _candidates.FirstOrDefault(c => c.VirtualKey == "AUTO_ELEV_EAST")?.SelectedScale ?? 100;
            int autoElevWestScale = _candidates.FirstOrDefault(c => c.VirtualKey == "AUTO_ELEV_WEST")?.SelectedScale ?? 100;

            Dictionary<long, int> customViewScales = new Dictionary<long, int>();
            foreach (SheetCandidateItem c in _candidates)
            {
                if (c.ViewId != ElementId.InvalidElementId && c.SupportsScale)
                {
                    customViewScales[c.ViewId.Value] = c.SelectedScale;
                }
            }

            HashSet<long> selectedViewIds = _candidates
                .Where(c => c.IsSelected && c.ViewId != ElementId.InvalidElementId)
                .Select(c => c.ViewId.Value)
                .ToHashSet();

            bool replaceRoomTags = _replaceRoomTagsCheckBox.IsChecked == true;
            string preferredRoomTagType = (_roomTagTypeComboBox.SelectedItem as string) ?? "Номер Имя";
            bool renumberRooms = _renumberRoomsCheckBox.IsChecked == true;
            string roomNumberingMode = _roomNumberingModeComboBox.SelectedIndex switch
            {
                1 => RoomRenumberEngine.ModeContinuous,
                2 => RoomRenumberEngine.ModeFloorPrefixed,
                _ => RoomRenumberEngine.ModePerFloor
            };

            string projectAddress = string.IsNullOrWhiteSpace(_projectAddressTextBox.Text)
                ? "г. Алматы, ул. Сатпаева, 22"
                : _projectAddressTextBox.Text.Trim();

            string studentGroup = _studentGroupTextBox.Text.Trim();

            bool includeCover = _candidates.Count == 0 ||
                                _candidates.Any(c => c.VirtualKey == "SHEET_COVER" && c.IsSelected);
            bool includeSituation = _candidates.Count == 0 ||
                                    _candidates.Any(c => c.VirtualKey == "SHEET_SITUATION_PLAN" && c.IsSelected);
            bool includeRender = _candidates.Count == 0 ||
                                 _candidates.Any(c => c.VirtualKey == "SHEET_RENDER" && c.IsSelected);

            ResultData = new GostStampData(
                rawDev,
                FormattedDeveloperName: formattedDev,
                FormattedCheckerName: formattedChk,
                FormattedNormControllerName: formattedChk,
                FormattedApproverName: formattedGip,
                OrganizationName: org,
                ProjectName: proj,
                ProjectCode: code,
                IssueDateGost: dateGost,
                SelectedSitePlanStyle: chosenStyle,
                SelectedBoundaryShape: chosenShape,
                BuildingPurpose: rawPurpose,
                PurposeCategory: purposeCategory,
                StageCode: stageCode,
                IncludeSitePlanSheet: includeSitePlan,
                IncludeGeneralDataSheet: includeGeneralData,
                CreateAutoSection1: createAutoSec1,
                CreateAutoSection2: createAutoSec2,
                CreateAutoElevSouth: createAutoElevSouth,
                CreateAutoElevNorth: createAutoElevNorth,
                CreateAutoElevEast: createAutoElevEast,
                CreateAutoElevWest: createAutoElevWest,
                SelectedViewIds: selectedViewIds,
                AllCandidates: _candidates,
                SitePlanScale: sitePlanScale,
                AutoSection1Scale: autoSec1Scale,
                AutoSection2Scale: autoSec2Scale,
                AutoElevSouthScale: autoElevSouthScale,
                AutoElevNorthScale: autoElevNorthScale,
                AutoElevEastScale: autoElevEastScale,
                AutoElevWestScale: autoElevWestScale,
                CustomViewScales: customViewScales,
                ReplaceRoomTagsWithAdsk: replaceRoomTags,
                PreferredRoomTagType: preferredRoomTagType,
                RenumberRoomsSequentially: renumberRooms,
                RoomNumberingMode: roomNumberingMode,
                CustomSitePlanConfig: _customSitePlanConfig,
                ProjectAddress: projectAddress,
                StudentGroup: studentGroup,
                IncludeCoverSheet: includeCover,
                IncludeSituationPlanSheet: includeSituation,
                IncludeRenderSheet: includeRender);

            DialogResult = true;
            Close();
        }
    }
}
