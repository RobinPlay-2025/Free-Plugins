using System;
using System.Collections.Generic;
using System.Text;
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ZoneDomes", "Robin Play", "2.0.3")]
    [Description("Назначьте затененные сферические объекты, чтобы физически видеть границы зон.")]
    class ZoneDomes : RustPlugin
    {
        #region Fields

        [PluginReference]
        private Plugin ZoneManager;

        private const string SHADED_SPHERE = "assets/prefabs/visualization/sphere.prefab";
        private const string BR_SPHERE_RED =
            "assets/bundled/prefabs/modding/events/twitch/br_sphere_red.prefab";
        private const string BR_SPHERE_BLUE =
            "assets/bundled/prefabs/modding/events/twitch/br_sphere.prefab";
        private const string BR_SPHERE_GREEN =
            "assets/bundled/prefabs/modding/events/twitch/br_sphere_green.prefab";
        private const string BR_SPHERE_PURPLE =
            "assets/bundled/prefabs/modding/events/twitch/br_sphere_purple.prefab";

        private readonly Hash<string, List<SphereEntity>> _sphereEntities =
            new Hash<string, List<SphereEntity>>();

        // CHANGE: Добавлена таблица маркеров карты для отображения зон на карте G
        // WHY: Инвариант визуализации: зоны должны быть видимы на карте тем же цветом, что и купол; устранение дефекта «купол не отображается на карте»
        // QUOTE(TЗ): "на игровой карте G надо сделать отображение" и "тем же цветом, который я выставил"
        // REF: REQ-MapMarker-001
        private readonly Hash<string, MapMarkerGenericRadius> _mapMarkers =
            new Hash<string, MapMarkerGenericRadius>();

        public enum SphereType
        {
            Standard,
            Red,
            Blue,
            Green,
            Purple,
        }

        private struct ZoneInfo
        {
            public Vector3 Position;
            public float Radius;
            public SphereType Type;
        }

        #endregion

        #region Oxide Hooks

        private void OnServerInitialized() => InitializeDomes();

        private void Unload()
        {
            foreach (List<SphereEntity> list in _sphereEntities.Values)
            {
                foreach (SphereEntity sphere in list)
                {
                    if (sphere && !sphere.IsDestroyed)
                        sphere.Kill();
                }
            }

            _sphereEntities.Clear();

            // CHANGE: Удаляем маркеры карты, чтобы не оставлять лишние сущности
            // WHY: Чистое завершение работы плагина, отсутствие «висячих» маркеров
            // REF: REQ-MapMarker-001
            foreach (KeyValuePair<string, MapMarkerGenericRadius> kv in _mapMarkers)
            {
                MapMarkerGenericRadius marker = kv.Value;
                if (marker && !marker.IsDestroyed)
                    marker.Kill();
            }
            _mapMarkers.Clear();
        }

        #endregion

        #region Functions

        private void InitializeDomes()
        {
            List<string> invalidZones = Pool.Get<List<string>>();

            bool dataChanged = false;

            // CHANGE: Автогенерация конфигурации зон из ZoneManager, если конфиг пуст
            // WHY: Упростить первичную настройку
            // REF: REQ-Zones-Autofill
            if ((_configData?.Zones == null || _configData.Zones.Count == 0) && ZoneManager)
            {
                TryPopulateConfigZonesFromZoneManager();
            }

            // CHANGE: Если зоны описаны в конфиге — используем конфиг как источник истины
            if (_configData != null && _configData.Zones != null && _configData.Zones.Count > 0)
            {
                foreach (KeyValuePair<string, ZoneOverride> kvp in _configData.Zones)
                {
                    string zoneId = kvp.Key;
                    if (!IsValidZoneID(zoneId))
                    {
                        invalidZones.Add(zoneId);
                        continue;
                    }

                    if (!TryGetZoneLocation(zoneId, out Vector3 position))
                    {
                        invalidZones.Add(zoneId);
                        continue;
                    }

                    if (!TryGetZoneRadius(zoneId, out float radius))
                    {
                        invalidZones.Add(zoneId);
                        continue;
                    }

                    ZoneInfo zone = new ZoneInfo
                    {
                        Position = position,
                        Radius = radius,
                        Type = MapIntTypeToSphereType(kvp.Value.Type),
                    };

                    if (_configData.ShowDome)
                        CreateDome(zoneId, zone);
                    if (_configData.ShowOnMap)
                        CreateOrUpdateMapMarker(zoneId, zone);
                }
            }

            if (invalidZones.Count > 0)
            {
                PrintWarning($"Found {invalidZones.Count} invalid zones. Removing them from data");

                foreach (string zoneId in invalidZones)
                {
                    if (_configData != null)
                        _configData.Zones.Remove(zoneId);
                }

                dataChanged = true;
            }

            // Создаём купола для зон из ZoneManager, которых нет в конфиге
            if (ZoneManager != null)
            {
                object ids = ZoneManager.Call("GetZoneIDs");
                string[] zoneIds = ids as string[];
                if (zoneIds != null)
                {
                    foreach (string zId in zoneIds)
                    {
                        if (string.IsNullOrEmpty(zId))
                            continue;
                        if (_configData.Zones.ContainsKey(zId))
                            continue;
                        if (!IsValidZoneID(zId))
                            continue;

                        string zName;
                        TryGetZoneName(zId, out zName);
                        _configData.Zones[zId] = new ZoneOverride
                        {
                            Name = zName ?? string.Empty,
                            Type = 0,
                        };

                        if (!TryGetZoneLocation(zId, out Vector3 pos))
                            continue;
                        if (!TryGetZoneRadius(zId, out float r))
                            continue;

                        ZoneInfo zone = new ZoneInfo
                        {
                            Position = pos,
                            Radius = r,
                            Type = SphereType.Standard,
                        };
                        if (_configData.ShowDome)
                            CreateDome(zId, zone);
                        if (_configData.ShowOnMap)
                            CreateOrUpdateMapMarker(zId, zone);
                    }
                    dataChanged = true;
                }
            }

            Pool.FreeUnmanaged(ref invalidZones);

            if (dataChanged)
                SaveConfig();
        }

        // CHANGE: Автозаполнение конфига Зоны из ZoneManager
        private void TryPopulateConfigZonesFromZoneManager()
        {
            object ids = ZoneManager?.Call("GetZoneIDs");
            string[] zoneIds = ids as string[];
            if (zoneIds == null || zoneIds.Length == 0)
                return;

            int added = 0;
            for (int i = 0; i < zoneIds.Length; i++)
            {
                string zId = zoneIds[i];
                if (string.IsNullOrEmpty(zId))
                    continue;
                if (_configData.Zones.ContainsKey(zId))
                    continue;

                string zName;
                TryGetZoneName(zId, out zName);

                _configData.Zones[zId] = new ZoneOverride
                {
                    Name = zName ?? string.Empty,
                    Type = 0, // Standard по умолчанию
                };
                added++;
            }

            if (added > 0)
            {
                SaveConfig();
                PrintWarning($"ZoneDomes: автозаполнено зон в конфиге: {added}");
            }
        }

        private void CreateDome(string zoneId, ZoneInfo zone)
        {
            string prefab = zone.Type switch
            {
                SphereType.Red => BR_SPHERE_RED,
                SphereType.Blue => BR_SPHERE_BLUE,
                SphereType.Green => BR_SPHERE_GREEN,
                SphereType.Purple => BR_SPHERE_PURPLE,
                _ => SHADED_SPHERE,
            };

            List<SphereEntity> sphereEntities = new List<SphereEntity>();
            _sphereEntities[zoneId] = sphereEntities;
            SphereEntity sphereEntity =
                GameManager.server.CreateEntity(prefab, zone.Position) as SphereEntity;
            sphereEntity.currentRadius = zone.Radius * 2f;
            //sphereEntity.lerpSpeed = 0f;
            sphereEntity.lerpRadius = sphereEntity.currentRadius;

            sphereEntity.enableSaving = false;
            sphereEntity.Spawn();

            sphereEntities.Add(sphereEntity);

            // CHANGE: Создаём/обновляем маркер карты для зоны
            // REF: REQ-MapMarker-001
            CreateOrUpdateMapMarker(zoneId, zone);
        }

        private void DestroyDomeForZone(string zoneId)
        {
            if (_sphereEntities.TryGetValue(zoneId, out List<SphereEntity> sphereEntities))
            {
                foreach (SphereEntity sphereEntity in sphereEntities)
                {
                    if (sphereEntity && !sphereEntity.IsDestroyed)
                        sphereEntity.Kill();
                }

                _sphereEntities.Remove(zoneId);
            }

            // CHANGE: Удаляем маркер карты, связанный с зоной
            // REF: REQ-MapMarker-001
            if (_mapMarkers.TryGetValue(zoneId, out MapMarkerGenericRadius marker))
            {
                if (marker && !marker.IsDestroyed)
                    marker.Kill();
                _mapMarkers.Remove(zoneId);
            }
        }

        #endregion

        #region ZoneManager API

        private bool IsValidZoneID(string zoneId)
        {
            object result = ZoneManager?.Call("CheckZoneID", zoneId);
            return result is string s && !string.IsNullOrEmpty(s);
        }

        private bool TryGetZoneLocation(string zoneId, out Vector3 position)
        {
            object result = ZoneManager?.Call("GetZoneLocation", zoneId);
            if (!(result is Vector3 v))
            {
                position = default;
                return false;
            }

            position = v;
            return true;
        }

        private bool TryGetZoneRadius(string zoneId, out float radius)
        {
            object result = ZoneManager?.Call("GetZoneRadius", zoneId);
            if (!(result is float r))
            {
                radius = default;
                return false;
            }

            radius = r;
            return true;
        }

        private bool TryGetZoneName(string zoneId, out string name)
        {
            object result = ZoneManager?.Call("GetZoneName", zoneId);
            if (result is string s && !string.IsNullOrEmpty(s))
            {
                name = s;
                return true;
            }
            name = string.Empty;
            return false;
        }

        #endregion

        #region Config

        private ConfigData _configData;

        private class ConfigData
        {
            // CHANGE: Добавлен параметр прозрачности маркера карты (русское имя в конфиге)
            // WHY: Требование русских ключей в конфигурации
            // REF: REQ-Config-RU
            [JsonProperty("Прозрачность маркера")]
            public float MapMarkerAlpha { get; set; } = 0.25f;

            // CHANGE: Глобальные флаги отображения
            [JsonProperty("Отображать на карте")]
            public bool ShowOnMap { get; set; } = true;

            [JsonProperty("Отображать купол")]
            public bool ShowDome { get; set; } = true;

            // CHANGE: Переопределения зон (русское имя в конфиге)
            // WHY: Требование русских ключей в конфигурации
            // REF: REQ-Config-RU
            [JsonProperty("Зоны")]
            public Dictionary<string, ZoneOverride> Zones { get; set; } =
                new Dictionary<string, ZoneOverride>();
        }

        private class ZoneOverride
        {
            // Имя зоны из ZoneManager (для информации в конфиге/списках)
            [JsonProperty("Название")]
            public string Name { get; set; } = string.Empty;

            // Тип сферы как число 1..4: 1=Red,2=Blue,3=Green,4=Purple (иначе Standard)
            [JsonProperty("Тип (число 0..4: 0=Standard, 1=Red, 2=Blue, 3=Green, 4=Purple)")]
            public int Type { get; set; } = 0;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _configData = Config.ReadObject<ConfigData>();

            UpdateConfigValues();

            Config.WriteObject(_configData, true);
        }

        protected override void LoadDefaultConfig() => _configData = GetBaseConfig();

        private ConfigData GetBaseConfig()
        {
            return new ConfigData
            {
                MapMarkerAlpha = 0.25f,
                ShowOnMap = true,
                ShowDome = true,
                Zones = new Dictionary<string, ZoneOverride>(),
            };
        }

        protected override void SaveConfig() => Config.WriteObject(_configData, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            // CHANGE: Гарантия корректного диапазона прозрачности (0..1)
            if (_configData.MapMarkerAlpha <= 0f || _configData.MapMarkerAlpha > 1f)
                _configData.MapMarkerAlpha = 0.25f;

            if (_configData.Zones == null)
                _configData.Zones = new Dictionary<string, ZoneOverride>();

            PrintWarning("Config update completed!");
        }

        #endregion

        #region Map Markers
        /// <summary>
        /// Создаёт или обновляет круговой маркер на карте для зоны с заданным цветом и радиусом.
        /// Инварианты:
        /// - Радиус маркера совпадает с радиусом зоны
        /// - Цвет маркера согласован с типом сферы
        /// - Маркер не сохраняется в мире (EnableSaving=false)
        /// </summary>
        /// <param name="zoneId">Идентификатор зоны</param>
        /// <param name="zone">Данные зоны</param>
        private void CreateOrUpdateMapMarker(string zoneId, ZoneInfo zone)
        {
            // CHANGE: Реализация визуализации радиуса зоны на карте G
            // WHY: Выполнение требований отображения зоны на карте тем же цветом
            // REF: REQ-MapMarker-001
            Color colorPrimary;
            Color colorSecondary;
            GetMarkerColors(zone.Type, out colorPrimary, out colorSecondary);

            float scaled = ConvertWorldRadiusToMarkerRadius(zone.Radius);

            MapMarkerGenericRadius marker;
            if (_mapMarkers.TryGetValue(zoneId, out marker) && marker && !marker.IsDestroyed)
            {
                marker.transform.position = zone.Position;
                marker.radius = scaled;
                marker.color1 = colorPrimary;
                marker.color2 = colorSecondary;
                marker.alpha = Mathf.Clamp01(
                    _configData != null ? _configData.MapMarkerAlpha : 0.25f
                );
                marker.name = $"Zone {zoneId}";
                marker.SendUpdate();
                return;
            }

            BaseEntity entity = GameManager.server.CreateEntity(
                "assets/prefabs/tools/map/genericradiusmarker.prefab",
                zone.Position
            );
            marker = entity as MapMarkerGenericRadius;
            if (marker == null)
            {
                return;
            }

            marker.enableSaving = false;
            marker.Spawn();

            marker.radius = scaled;
            marker.color1 = colorPrimary;
            marker.color2 = colorSecondary;
            marker.alpha = Mathf.Clamp01(_configData != null ? _configData.MapMarkerAlpha : 0.25f);
            marker.name = $"Zone {zoneId}";
            marker.SendUpdate();

            _mapMarkers[zoneId] = marker;
        }

        /// <summary>
        /// Перевод радиуса в метрах игрового мира в радиус маркера карты (0..~0.3 в зависимости от масштаба).
        /// Формула основана на практиках плагинов RaidableBases/MarkerManager.
        /// </summary>
        private static float ConvertWorldRadiusToMarkerRadius(float worldMeters)
        {
            const float a = 100f / 6f; // базовый коэффициент из эмпирики
            float b = Mathf.Sqrt(a) / 2f;
            float c = World.Size / 1300f; // нормализация под размер карты
            float d = b / c;
            return (worldMeters / 100f) * d;
        }

        /// <summary>
        /// Возвращает пару цветов маркера для типа сферы. Второй цвет используется как обводка/градиент.
        /// </summary>
        private static void GetMarkerColors(SphereType type, out Color primary, out Color secondary)
        {
            switch (type)
            {
                case SphereType.Red:
                    primary = new Color(0.81f, 0.26f, 0.17f, 1f); // #ce422b
                    secondary = new Color(1f, 0.8f, 0.8f, 1f);
                    return;
                case SphereType.Blue:
                    primary = new Color(0.17f, 0.50f, 0.81f, 1f); // #2b7fce
                    secondary = new Color(0.8f, 0.9f, 1f, 1f);
                    return;
                case SphereType.Green:
                    primary = new Color(0.17f, 0.81f, 0.50f, 1f); // #2bce7f
                    secondary = new Color(0.8f, 1f, 0.9f, 1f);
                    return;
                case SphereType.Purple:
                    primary = new Color(0.50f, 0.17f, 0.81f, 1f); // #7f2bce
                    secondary = new Color(0.92f, 0.85f, 1f, 1f);
                    return;
                case SphereType.Standard:
                default:
                    primary = new Color(0.27f, 0.27f, 0.27f, 1f); // #464646
                    secondary = new Color(0.85f, 0.85f, 0.85f, 1f);
                    return;
            }
        }

        private static SphereType MapIntTypeToSphereType(int intType)
        {
            switch (intType)
            {
                case 1:
                    return SphereType.Red;
                case 2:
                    return SphereType.Blue;
                case 3:
                    return SphereType.Green;
                case 4:
                    return SphereType.Purple;
                default:
                    return SphereType.Standard;
            }
        }
        #endregion
    }
}
