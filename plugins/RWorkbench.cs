//v1.4.4 Фикс после обновления август 2025

using System;
using System.Collections.Generic;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RWorkbench", "Robin Play", "1.4.4")]
    [Description("Расширяет радиус действия верстака на все здание")]
    public class RWorkbench : RustPlugin
    {
        #region Class Fields
        /// <summary>
        /// Plugin Config
        /// </summary>
        private PluginConfig? _pluginConfig;

        private GameObject? _go;
        private RWorkbenchTrigger? _tb;

        private const string UsePermission = "rworkbench.use";

        private readonly Hash<ulong, PlayerData> _playerData = new();
        private readonly Hash<uint, BuildingData> _buildingData = new();
        private float _scanRange;
        private float _halfScanRange;

        private PhysicsScene _physics;
        private int _currentPlayerIndex;
        private readonly int _batchSize = 5;

        #endregion Class Fields

        #region Setup & Loading
        private void Init()
        {
            permission.RegisterPermission(UsePermission, this);

            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnEntityKill));

            _scanRange = _pluginConfig?.BaseDistance ?? 3f;
            _halfScanRange = _scanRange / 2f;
        }

        protected override void LoadDefaultMessages()
        {
            // Русские сообщения
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    [LangKeys.Notification] =
                        "Радиус действия вашего верстака был увеличен для работы внутри всего здания",
                },
                this,
                "ru"
            );

            // Английские сообщения (по умолчанию)
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    [LangKeys.Notification] =
                        "Your workbench range has been increased to work inside your building",
                },
                this
            );
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Создание новой конфигурации");
            _pluginConfig = new PluginConfig
            {
                BuiltNotification = true,
                UpdateRate = 3f,
                FastBuildingCheck = false,
                BaseDistance = 16f,
                RequiredDistance = 5f,
            };

            Config.WriteObject(_pluginConfig);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
            _pluginConfig = Config.ReadObject<PluginConfig>();

            // Создаем новую конфигурацию если в файле отсутствуют некоторые значения
            bool needsSave = false;

            // Убедимся, что уведомления включены
            if (!_pluginConfig.BuiltNotification)
            {
                _pluginConfig.BuiltNotification = true;
                needsSave = true;
            }

            if (_pluginConfig.UpdateRate <= 0f)
            {
                _pluginConfig.UpdateRate = 3f;
                needsSave = true;
            }

            if (_pluginConfig.BaseDistance <= 0f)
            {
                _pluginConfig.BaseDistance = 16f;
                needsSave = true;
            }

            if (_pluginConfig.RequiredDistance <= 0f)
            {
                _pluginConfig.RequiredDistance = 5f;
                needsSave = true;
            }

            if (needsSave)
            {
                Config.WriteObject(_pluginConfig);
            }
        }

        private void OnServerInitialized(bool initial)
        {
            _physics = Physics.defaultPhysicsScene;
            if (_pluginConfig?.BaseDistance < 3f)
            {
                _pluginConfig.BaseDistance = 3f;
            }

            _go = new GameObject("RWorkbenchObject");
            _tb = _go.AddComponent<RWorkbenchTrigger>();
            _ = _go.AddComponent<WorkbenchBehavior>();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                OnPlayerConnected(player);
            }

            _ = timer.Every(_pluginConfig?.UpdateRate ?? 3f, StartUpdatingWorkbench);

            Subscribe(nameof(OnEntitySpawned));
            Subscribe(nameof(OnEntityKill));
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            player.nextCheckTime = float.MaxValue;
            if (_tb != null)
            {
                _ = player.EnterTrigger(_tb);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string? reason)
        {
            if (player == null)
            {
                return;
            }

            player.nextCheckTime = 0;
            player.cachedCraftLevel = 0;
            Hash<uint, BuildingData>? playerData = _playerData[player.userID]?.BuildingData;
            if (playerData != null)
            {
                foreach (BuildingData data in playerData.Values)
                {
                    data.LeaveBuilding(player);
                }
            }

            _ = _playerData.Remove(player.userID);
            if (_tb != null)
            {
                player.LeaveTrigger(_tb);
            }
        }

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                OnPlayerDisconnected(player, null);
            }

            UnityEngine.Object.Destroy(_go);
        }
        #endregion Setup & Loading

        #region Workbench Handler
        /// <summary>
        /// Запускает обновление верстаков для всех активных игроков
        /// </summary>
        public void StartUpdatingWorkbench()
        {
            if (BasePlayer.activePlayerList.Count == 0)
            {
                return;
            }

            _currentPlayerIndex = 0;
            ProcessNextPlayerBatch();
        }

        /// <summary>
        /// Обрабатывает очередную партию игроков для обновления данных верстаков
        /// </summary>
        private void ProcessNextPlayerBatch()
        {
            if (_currentPlayerIndex >= BasePlayer.activePlayerList.Count)
            {
                return; // Обработка завершена
            }

            int endIndex = Math.Min(
                _currentPlayerIndex + _batchSize,
                BasePlayer.activePlayerList.Count
            );

            for (int i = _currentPlayerIndex; i < endIndex; i++)
            {
                BasePlayer player = BasePlayer.activePlayerList[i];
                if (player == null)
                {
                    continue;
                }

                if (!HasPermission(player, UsePermission))
                {
                    if (Math.Abs(player.nextCheckTime - float.MaxValue) < 0.0001f) // Сравнение с плавающей точкой через диапазон
                    {
                        player.nextCheckTime = 0;
                        player.cachedCraftLevel = 0;
                    }

                    continue;
                }

                PlayerData data = GetPlayerData(player.userID);
                if (
                    Vector3.Distance(player.transform.position, data.Position)
                    < (_pluginConfig?.RequiredDistance ?? 5f)
                )
                {
                    continue;
                }

                if (player.triggers == null && _tb != null)
                {
                    _ = player.EnterTrigger(_tb);
                }

                data.Position = player.transform.position;

                UpdatePlayerBuildings(player, data);
            }

            _currentPlayerIndex = endIndex;

            // Если есть еще игроки для обработки, запланируем следующую партию
            if (_currentPlayerIndex < BasePlayer.activePlayerList.Count)
            {
                _ = timer.Once(0.05f, ProcessNextPlayerBatch);
            }
        }

        public void UpdatePlayerBuildings(BasePlayer player, PlayerData data)
        {
            List<uint> currentBuildings = Pool.Get<List<uint>>();

            if (_pluginConfig?.FastBuildingCheck ?? false)
            {
                GetNearbyAuthorizedBuildingsFast(player, currentBuildings);
            }
            else
            {
                GetNearbyAuthorizedBuildings(player, currentBuildings);
            }

            List<uint> leftBuildings = Pool.Get<List<uint>>();
            foreach (uint buildingId in data.BuildingData.Keys)
            {
                if (!currentBuildings.Contains(buildingId))
                {
                    leftBuildings.Add(buildingId);
                }
            }

            for (int index = 0; index < leftBuildings.Count; index++)
            {
                uint leftBuilding = leftBuildings[index];
                OnPlayerLeftBuilding(player, leftBuilding);
            }

            for (int index = 0; index < currentBuildings.Count; index++)
            {
                uint currentBuilding = currentBuildings[index];
                if (!data.BuildingData.ContainsKey(currentBuilding))
                {
                    OnPlayerEnterBuilding(player, currentBuilding);
                }
            }

            UpdatePlayerWorkbenchLevel(player);

            Pool.FreeUnmanaged(ref currentBuildings);
            Pool.FreeUnmanaged(ref leftBuildings);
        }

        public void OnPlayerEnterBuilding(BasePlayer player, uint buildingId)
        {
            BuildingData building = GetBuildingData(buildingId);
            building.EnterBuilding(player);
            Hash<uint, BuildingData> playerBuildings = GetPlayerData(player.userID).BuildingData;
            playerBuildings[buildingId] = building;
        }

        public void OnPlayerLeftBuilding(BasePlayer player, uint buildingId)
        {
            BuildingData building = GetBuildingData(buildingId);
            building.LeaveBuilding(player);
            Hash<uint, BuildingData> playerBuildings = GetPlayerData(player.userID).BuildingData;
            _ = playerBuildings.Remove(buildingId);
        }
        #endregion Workbench Handler

        #region Oxide Hooks
        private void OnEntitySpawned(Workbench bench)
        {
            if (bench == null)
            {
                return;
            }

            //Needs to be in NextTick since other plugins can spawn Workbenches
            NextTick(() =>
            {
                if (bench == null) // Проверяем не уничтожен ли верстак после NextTick
                {
                    return;
                }

                // Проверяем, не является ли верстак переработчиком из IQRecycler
                if (bench.skinID != 0)
                {
                    return;
                }

                BuildingData building = GetBuildingData(bench.buildingID);
                building.OnBenchBuilt(bench);
                UpdateBuildingPlayers(building);

                if (_pluginConfig?.BuiltNotification != true)
                {
                    return;
                }

                BasePlayer player = BasePlayer.FindByID(bench.OwnerID);
                if (!player)
                {
                    return;
                }

                if (!HasPermission(player, UsePermission))
                {
                    return;
                }

                // Получаем купол игрока
                BuildingPrivlidge? priv = bench.GetBuildingPrivilege();
                if (priv == null)
                {
                    return; // Верстак не в зоне купола
                }

                // Показываем уведомление с пустым сообщением, т.к. текст уже на изображении
                ShowGameTip(player, 0f);
            });
        }

        private void OnEntityKill(Workbench bench)
        {
            if (bench == null)
            {
                return;
            }

            BuildingData building = GetBuildingData(bench.buildingID);
            building.OnBenchKilled(bench);
            UpdateBuildingPlayers(building);
        }

        private void OnEntityKill(BuildingPrivlidge tc)
        {
            if (tc == null)
            {
                return;
            }

            HandleCupboardClearList(tc);
        }

        private void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            if (privilege == null || player == null)
            {
                return;
            }

            OnPlayerEnterBuilding(player, privilege.buildingID);
            UpdatePlayerWorkbenchLevel(player);
        }

        private void OnCupboardDeauthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            if (privilege == null || player == null)
            {
                return;
            }

            OnPlayerLeftBuilding(player, privilege.buildingID);
            UpdatePlayerWorkbenchLevel(player);
        }

        /// <summary>
        /// Rust hook: called when a building privilege clear list RPC occurs
        /// </summary>
        /// <param name="buildingPrivlidge">Associated building privilege entity</param>
        /// <param name="rpcPlayer">Player who invoked the RPC</param>
        private void OnCupboardClearList(BuildingPrivlidge buildingPrivlidge, BasePlayer rpcPlayer)
        {
            if (buildingPrivlidge == null)
            {
                return;
            }

            HandleCupboardClearList(buildingPrivlidge);
        }

        /// <summary>
        /// Internal helper used by multiple hooks
        /// </summary>
        /// <param name="privilege">Building privilege that had its list cleared</param>
        private void HandleCupboardClearList(BuildingPrivlidge privilege)
        {
            if (privilege == null)
            {
                return;
            }

            BuildingData data = GetBuildingData(privilege.buildingID);
            for (int index = data.Players.Count - 1; index >= 0; index--)
            {
                BasePlayer player = data.Players[index];
                OnPlayerLeftBuilding(player, privilege.buildingID);
                UpdatePlayerWorkbenchLevel(player);
            }
        }

        private void OnEntityEnter(TriggerWorkbench trigger, BasePlayer player)
        {
            if (trigger == null || player == null)
            {
                return;
            }

            if (!player.IsNpc)
            {
                UpdatePlayerWorkbenchLevel(player);
            }
        }

        private void OnEntityLeave(TriggerWorkbench trigger, BasePlayer player)
        {
            if (trigger == null || player == null)
            {
                return;
            }

            if (!player.IsNpc)
            {
                NextTick(() => UpdatePlayerWorkbenchLevel(player));
            }
        }

        private void OnEntityLeave(RWorkbenchTrigger trigger, BasePlayer player)
        {
            if (trigger == null || player == null)
            {
                return;
            }

            if (player.IsNpc)
            {
                return;
            }

            if (_tb != null)
            {
                NextTick(() => player.EnterTrigger(_tb));
            }
        }
        #endregion Oxide Hooks

        #region Helper Methods
        public void UpdateBuildingPlayers(BuildingData building)
        {
            for (int index = 0; index < building.Players.Count; index++)
            {
                BasePlayer player = building.Players[index];
                UpdatePlayerWorkbenchLevel(player);
            }
        }

        public void UpdatePlayerWorkbenchLevel(BasePlayer player)
        {
            byte level = 0;
            Hash<uint, BuildingData>? playerBuildings = _playerData[player.userID]?.BuildingData;
            if (playerBuildings != null)
            {
                foreach (BuildingData building in playerBuildings.Values)
                {
                    level = Math.Max(level, building.GetBuildingLevel());
                }
            }

            if (level != 3 && player.triggers != null)
            {
                for (int index = 0; index < player.triggers.Count; index++)
                {
                    TriggerWorkbench? trigger = player.triggers[index] as TriggerWorkbench;
                    if (trigger?.parentBench != null)
                    {
                        level = Math.Max(level, (byte)trigger.parentBench.Workbenchlevel);
                    }
                }
            }

            if ((byte)player.cachedCraftLevel == level)
            {
                return;
            }

            player.nextCheckTime = float.MaxValue;
            player.cachedCraftLevel = level;
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, level == 1);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, level == 2);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, level == 3);
            player.SendNetworkUpdateImmediate();
        }

        public PlayerData GetPlayerData(ulong playerId)
        {
            PlayerData? data = _playerData[playerId];
            if (data == null)
            {
                data = new PlayerData();
                _playerData[playerId] = data;
            }

            return data;
        }

        public BuildingData GetBuildingData(uint buildingId)
        {
            BuildingData? data = _buildingData[buildingId];
            if (data == null)
            {
                data = new BuildingData(buildingId);
                _buildingData[buildingId] = data;
            }

            return data;
        }

        private readonly RaycastHit[] _hits = new RaycastHit[256];
        private readonly List<uint> _processedBuildings = new();

        public void GetNearbyAuthorizedBuildingsFast(BasePlayer player, List<uint> authorizedPrivs)
        {
            OBB obb = player.WorldSpaceBounds();
            float baseDistance = _scanRange;
            int amount = _physics.Raycast(
                player.transform.position + (Vector3.down * _halfScanRange),
                Vector3.up,
                _hits,
                baseDistance,
                Rust.Layers.Construction,
                QueryTriggerInteraction.Ignore
            );
            for (int index = 0; index < amount; index++)
            {
                BuildingBlock? block = _hits[index].transform.ToBaseEntity() as BuildingBlock;
                if (block == null)
                {
                    continue;
                }

                if (
                    _processedBuildings.Contains(block.buildingID)
                    || obb.Distance(block.WorldSpaceBounds()) > baseDistance
                )
                {
                    continue;
                }

                _processedBuildings.Add(block.buildingID);
                BuildingPrivlidge? priv = block.GetBuilding()?.GetDominatingBuildingPrivilege();
                if (priv?.IsAuthed(player) != true)
                {
                    continue;
                }

                authorizedPrivs.Add(priv.buildingID);
            }
            _processedBuildings.Clear();
        }

        public void GetNearbyAuthorizedBuildings(BasePlayer player, List<uint> authorizedPrivs)
        {
            OBB obb = player.WorldSpaceBounds();
            float baseDistance = _pluginConfig?.BaseDistance ?? 16f;
            int amount = _physics.OverlapSphere(
                obb.position,
                baseDistance + obb.extents.magnitude,
                Vis.colBuffer,
                Rust.Layers.Construction,
                QueryTriggerInteraction.Ignore
            );
            for (int index = 0; index < amount; index++)
            {
                Collider collider = Vis.colBuffer[index];
                BuildingBlock? block = collider.ToBaseEntity() as BuildingBlock;
                if (block == null)
                {
                    continue;
                }

                if (
                    _processedBuildings.Contains(block.buildingID)
                    || obb.Distance(block.WorldSpaceBounds()) > baseDistance
                )
                {
                    continue;
                }

                _processedBuildings.Add(block.buildingID);
                BuildingPrivlidge? priv = block.GetBuilding()?.GetDominatingBuildingPrivilege();
                if (priv?.IsAuthed(player) != true)
                {
                    continue;
                }

                authorizedPrivs.Add(priv.buildingID);
            }

            _processedBuildings.Clear();
        }

        private void ShowGameTip(BasePlayer player, float duration = 0f)
        {
            if (player?.IsConnected != true)
            {
                return;
            }

            // Показываем GameTip
            player.SendConsoleCommand(
                "gametip.showgametip",
                GetMessage(LangKeys.Notification, player.UserIDString)
            );

            // Скрываем его через 5 секунд
            _ = timer.Once(
                5f,
                () =>
                {
                    if (player?.IsConnected == true)
                    {
                        player.SendConsoleCommand("gametip.hidegametip");
                    }
                }
            );
        }

        public bool HasPermission(BasePlayer player, string perm)
        {
            return permission.UserHasPermission(player.UserIDString, perm);
        }

        private string GetMessage(string key, string? userId = null)
        {
            return lang.GetMessage(key, this, userId);
        }
        #endregion Helper Methods

        #region Building Data
        public class BuildingData
        {
            public uint BuildingId { get; }
            public Workbench? BestWorkbench { get; set; }
            public List<BasePlayer> Players { get; } = new();
            public List<Workbench> Workbenches { get; }

            public BuildingData(uint buildingId)
            {
                BuildingId = buildingId;
                Workbenches =
                    BuildingManager
                        .server.GetBuilding(buildingId)
                        ?.decayEntities.OfType<Workbench>()
                        .ToList() ?? new List<Workbench>();

                UpdateBestBench();
            }

            public void EnterBuilding(BasePlayer player)
            {
                Players.Add(player);
            }

            public void LeaveBuilding(BasePlayer player)
            {
                _ = Players.Remove(player);
            }

            public void OnBenchBuilt(Workbench workbench)
            {
                Workbenches.Add(workbench);
                UpdateBestBench();
            }

            public void OnBenchKilled(Workbench workbench)
            {
                _ = Workbenches.Remove(workbench);
                UpdateBestBench();
            }

            public byte GetBuildingLevel()
            {
                if (BestWorkbench == null)
                {
                    return 0;
                }

                return (byte)BestWorkbench.Workbenchlevel;
            }

            private void UpdateBestBench()
            {
                BestWorkbench = null;
                for (int index = 0; index < Workbenches.Count; index++)
                {
                    Workbench workbench = Workbenches[index];
                    if (
                        BestWorkbench == null
                        || BestWorkbench.Workbenchlevel < workbench.Workbenchlevel
                    )
                    {
                        BestWorkbench = workbench;
                    }
                }
            }
        }
        #endregion Building Data

        #region Classes
        private sealed class PluginConfig
        {
            [JsonProperty(PropertyName = "Отображать уведомление о создании верстака")]
            public bool BuiltNotification = true;

            [JsonProperty(PropertyName = "Частота проверки нахождения внутри здания (секунды)")]
            public float UpdateRate = 3f;

            [JsonProperty(
                PropertyName = "Включить быструю проверку здания (Проверяет только над и под игроком)"
            )]
            public bool FastBuildingCheck;

            [JsonProperty(
                PropertyName = "Расстояние от базы, чтобы считаться внутри здания (метры)"
            )]
            public float BaseDistance = 16f;

            [JsonProperty(PropertyName = "Требуемое расстояние от последнего обновления (метры)")]
            public float RequiredDistance = 5;
        }

        public class PlayerData
        {
            public Vector3 Position { get; set; }
            public Hash<uint, BuildingData> BuildingData { get; } = new();
        }

        private static class LangKeys
        {
            public const string Notification = nameof(Notification);
        }

        /// <summary>
        /// Поведение компонента верстака для здания
        /// </summary>
        public class WorkbenchBehavior : FacepunchBehaviour { }

        /// <summary>
        /// Триггер для расширения зоны действия верстака на всё здание
        /// </summary>
        public class RWorkbenchTrigger : TriggerBase
        {
            /// <summary>
            /// Список игроков, находящихся в зоне действия триггера
            /// </summary>
            private readonly List<BasePlayer> _activePlayers = new();

            /// <summary>
            /// Отслеживает список игроков, находящихся в зоне действия триггера
            /// </summary>
            public IReadOnlyList<BasePlayer> ActivePlayers => _activePlayers;
        }
        #endregion Classes
    }
}
