using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitViewPlacer
{
    /// <summary>
    /// Результат выполнения операции замены / размещения марок помещений.
    /// </summary>
    public sealed record RoomTagReplacementResult(
        int ReplacedCount,
        int CreatedCount,
        string FamilyName,
        string TypeName,
        string Message);

    /// <summary>
    /// Менеджер управления марками помещений: загрузка ADSK-семейства 
    /// "ADSK_Марка_Помещение_ДваЗначения" и замена/простановка марок в проекте.
    /// </summary>
    public static class RoomTagManager
    {
        public const string AdskRoomTagFamilyName = "ADSK_Марка_Помещение_ДваЗначения";
        public const string AdskRoomTagFileName = "ADSK_Марка_Помещение_ДваЗначения.rfa";
        public const string EmbeddedAdskResourceName = "RevitViewPlacer.RoomTagADSK.rfa";

        public static readonly string[] StandardAdskTypes =
        [
            "Номер Имя",
            "Номер Площадь",
            "Имя Площадь",
            "Номер Категория",
            "Номер",
            "Имя",
            "Номер Объем",
            "Имя Объем"
        ];

        /// <summary>
        /// Находит в документе или загружает из ресурсов семейство ADSK-марки помещений.
        /// </summary>
        public static FamilySymbol? FindOrLoadAdskRoomTagSymbol(
            Document doc,
            string preferredTypeName = "Номер Имя")
        {
            // 1. Поиск уже загруженного типоразмера марки помещений в документе
            FamilySymbol? symbol = FindLoadedSymbol(doc, preferredTypeName);
            if (symbol != null)
            {
                ActivateSymbolIfNeeded(doc, symbol);
                return symbol;
            }

            // 2. Если семейство еще не загружено, загружаем его из файла рядом с DLL или из встроенных ресурсов
            string? rfaPath = ResolveAdskRoomTagRfaPath();
            if (!string.IsNullOrEmpty(rfaPath) && File.Exists(rfaPath))
            {
                try
                {
                    doc.LoadFamily(rfaPath, new AdskFamilyLoadOptions(), out _);
                    doc.Regenerate();
                }
                catch
                {
                    // Игнорируем ошибки повторной загрузки
                }

                symbol = FindLoadedSymbol(doc, preferredTypeName);
                if (symbol != null)
                {
                    ActivateSymbolIfNeeded(doc, symbol);
                    return symbol;
                }
            }

            // 3. Резервный поиск любого существующего типоразмера марки помещений в проекте
            List<FamilySymbol> fallbackSymbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .ToList();

            return fallbackSymbols.FirstOrDefault(s =>
                       s.FamilyName.Contains("ADSK", StringComparison.OrdinalIgnoreCase) ||
                       s.FamilyName.Contains("Помещен", StringComparison.OrdinalIgnoreCase))
                   ?? fallbackSymbols.FirstOrDefault();
        }

        /// <summary>
        /// Заменяет все существующие марки помещений на выбранных видах (или во всем проекте)
        /// на семейство ADSK_Марка_Помещение_ДваЗначения, а также опционально маркирует неразмеченные помещения.
        /// </summary>
        public static RoomTagReplacementResult ReplaceOrPlaceRoomTags(
            Document doc,
            IEnumerable<View>? targetViews = null,
            string preferredTypeName = "Номер Имя",
            bool tagUntaggedRooms = true)
        {
            FamilySymbol? adskSymbol = FindOrLoadAdskRoomTagSymbol(doc, preferredTypeName);
            if (adskSymbol == null)
            {
                return new RoomTagReplacementResult(
                    0, 0, AdskRoomTagFamilyName, preferredTypeName,
                    "Не удалось загрузить или найти семейство марки помещений " + AdskRoomTagFamilyName);
            }

            ActivateSymbolIfNeeded(doc, adskSymbol);

            List<View> viewsList = targetViews != null
                ? targetViews.Where(v => v is { IsTemplate: false } &&
                                         v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan).ToList()
                : [];

            HashSet<ElementId>? targetViewIds = viewsList.Count > 0
                ? viewsList.Select(v => v.Id).ToHashSet()
                : null;

            int replacedCount = 0;
            int createdCount = 0;

            // А) Замена всех существующих марок помещений на ADSK
            FilteredElementCollector tagCollector = targetViewIds != null && targetViewIds.Count == 1
                ? new FilteredElementCollector(doc, targetViewIds.First())
                : new FilteredElementCollector(doc);

            List<RoomTag> existingTags = tagCollector
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsNotElementType()
                .OfType<RoomTag>()
                .Where(tag => targetViewIds == null || targetViewIds.Contains(tag.OwnerViewId))
                .ToList();

            foreach (RoomTag tag in existingTags)
            {
                try
                {
                    if (tag.GetTypeId() != adskSymbol.Id)
                    {
                        tag.ChangeTypeId(adskSymbol.Id);
                        replacedCount++;
                    }
                }
                catch
                {
                    // Некоторые марки могут быть заблокированы связями
                }
            }

            // Б) Маркировка неразмеченных помещений на планах этажей
            if (tagUntaggedRooms)
            {
                // Если список видов не передан, собираем все печатные планы этажей проекта
                List<View> planViewsToTag = viewsList.Count > 0
                    ? viewsList
                    : new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .Where(v => !v.IsTemplate && v.CanBePrinted && v.ViewType == ViewType.FloorPlan)
                        .Cast<View>()
                        .ToList();

                foreach (View planView in planViewsToTag)
                {
                    // Находим все существующие марки помещений на данном виде
                    HashSet<ElementId> taggedRoomIdsOnView = new FilteredElementCollector(doc, planView.Id)
                        .OfCategory(BuiltInCategory.OST_RoomTags)
                        .WhereElementIsNotElementType()
                        .OfType<RoomTag>()
                        .Select(t => t.Room?.Id ?? ElementId.InvalidElementId)
                        .Where(id => id != ElementId.InvalidElementId)
                        .ToHashSet();

                    // Находим все размещенные помещения на данном виде/уровне
                    List<Room> roomsOnLevel = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .OfType<Room>()
                        .Where(r => r.Area > 0.001 && r.Location != null)
                        .Where(r => planView.GenLevel == null || r.LevelId == planView.GenLevel.Id)
                        .ToList();

                    foreach (Room room in roomsOnLevel)
                    {
                        if (taggedRoomIdsOnView.Contains(room.Id))
                        {
                            continue;
                        }

                        XYZ? roomPoint = GetRoomTagInsertionPoint(room);
                        if (roomPoint == null)
                        {
                            continue;
                        }

                        try
                        {
                            UV uvPoint = new UV(roomPoint.X, roomPoint.Y);
                            LinkElementId linkRoomId = new LinkElementId(room.Id);
                            RoomTag newTag = doc.Create.NewRoomTag(linkRoomId, uvPoint, planView.Id);
                            if (newTag != null)
                            {
                                if (newTag.GetTypeId() != adskSymbol.Id)
                                {
                                    newTag.ChangeTypeId(adskSymbol.Id);
                                }
                                taggedRoomIdsOnView.Add(room.Id);
                                createdCount++;
                            }
                        }
                        catch
                        {
                            // Если вид не поддерживает создание марки в данной точке
                        }
                    }
                }
            }

            string summaryMsg = $"Заменено марок помещений: {replacedCount} шт. " +
                                (tagUntaggedRooms ? $"Размещено новых марок: {createdCount} шт." : string.Empty);

            return new RoomTagReplacementResult(
                replacedCount,
                createdCount,
                adskSymbol.FamilyName,
                adskSymbol.Name,
                summaryMsg);
        }

        private static XYZ? GetRoomTagInsertionPoint(Room room)
        {
            if (room.Location is LocationPoint lp)
            {
                return lp.Point;
            }

            try
            {
                BoundingBoxXYZ bbox = room.get_BoundingBox(null);
                if (bbox != null)
                {
                    return (bbox.Min + bbox.Max) * 0.5;
                }
            }
            catch
            {
                // Игнорируем ошибки геометрии
            }

            return null;
        }

        private static FamilySymbol? FindLoadedSymbol(Document doc, string preferredTypeName)
        {
            List<FamilySymbol> symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .Where(s => s.FamilyName.Equals(AdskRoomTagFamilyName, StringComparison.OrdinalIgnoreCase) ||
                            s.FamilyName.Contains("ADSK_Марка_Помещение", StringComparison.OrdinalIgnoreCase) ||
                            s.FamilyName.Contains("ДваЗначения", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (symbols.Count == 0)
            {
                return null;
            }

            return symbols.FirstOrDefault(s => s.Name.Equals(preferredTypeName, StringComparison.OrdinalIgnoreCase))
                   ?? symbols.FirstOrDefault(s => s.Name.Replace("_", " ").Equals(preferredTypeName.Replace("_", " "), StringComparison.OrdinalIgnoreCase))
                   ?? symbols.FirstOrDefault(s => s.Name.Contains("Номер", StringComparison.OrdinalIgnoreCase) && s.Name.Contains("Имя", StringComparison.OrdinalIgnoreCase))
                   ?? symbols.FirstOrDefault(s => s.Name.Contains("Номер", StringComparison.OrdinalIgnoreCase) && s.Name.Contains("Площадь", StringComparison.OrdinalIgnoreCase))
                   ?? symbols[0];
        }

        private static void ActivateSymbolIfNeeded(Document doc, FamilySymbol symbol)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }
        }

        /// <summary>
        /// Возвращает путь к файлу "ADSK_Марка_Помещение_ДваЗначения.rfa" рядом с DLL
        /// или извлекает его из встроенных ресурсов сборки во временный файл.
        /// </summary>
        public static string? ResolveAdskRoomTagRfaPath()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string? assemblyDir = Path.GetDirectoryName(assembly.Location);

            // 1. Проверяем файл в папке плагина
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                string localPath = Path.Combine(assemblyDir, AdskRoomTagFileName);
                if (File.Exists(localPath))
                {
                    return localPath;
                }
            }

            // 2. Проверяем стандартную папку установки Addins
            string appDataAddins = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", "2026", "RevitViewPlacer", AdskRoomTagFileName);
            if (File.Exists(appDataAddins))
            {
                return appDataAddins;
            }

            // 3. Извлекаем из встроенного ресурса сборки
            using Stream? resourceStream = assembly.GetManifestResourceStream(EmbeddedAdskResourceName)
                                           ?? assembly.GetManifestResourceStream(AdskRoomTagFileName);
            if (resourceStream == null)
            {
                return null;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), AdskRoomTagFileName);
            try
            {
                using FileStream fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                resourceStream.CopyTo(fileStream);
                return tempPath;
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    return tempPath;
                }
                return null;
            }
        }

        private sealed class AdskFamilyLoadOptions : IFamilyLoadOptions
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
}
