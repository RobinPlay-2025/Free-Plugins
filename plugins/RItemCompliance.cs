//1.0.1 Добавлена блокировка одевания одежды
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RItemCompliance", "RobinPlay", "1.0.1")]
    [Description("Система соответствия предметов требованиям")]
    public partial class RItemCompliance : RustPlugin
    {
        #region Configuration

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty("Блокированные скины")]
            public Dictionary<string, List<ulong>> BlockedSkins { get; set; } =
                new Dictionary<string, List<ulong>>();

            [JsonProperty("Интервал урона (секунды)")]
            public float DamageInterval { get; set; } = 1.0f;

            [JsonProperty("Урон за интервал")]
            public float DamageAmount { get; set; } = 10.0f;

            [JsonProperty("Звук сирены")]
            public string SirenSound { get; set; } =
                "assets/prefabs/locks/keypad/effects/lock.code.denied.prefab";
        }

        protected override void LoadDefaultConfig()
        {
            configData = new ConfigData();
            configData.BlockedSkins["sleepingbag"] = new List<ulong> { 2921636205 };
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                {
                    LoadDefaultConfig();
                }
                else
                {
                    SaveConfig();
                }
            }
            catch
            {
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(configData);
        }

        #endregion

        #region Data

        private Dictionary<ulong, Timer> activeKillTimers = new Dictionary<ulong, Timer>();
        private Dictionary<ulong, Timer> activeClothingTimers = new Dictionary<ulong, Timer>();

        #endregion

        #region LanguageFile

        private void Print(BasePlayer player, string message)
        {
            Player.Message(player, message);
        }

        private string Lang(string key, string? id = null, params object[] args)
        {
            return args.Length == 0
                ? lang.GetMessage(key, this, id)
                : string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    lang.GetMessage(key, this, id),
                    args
                );
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["RestrictedItem"] =
                        "This item skin is prohibited by Russian law (Federal Law on Countering Extremist Activity)!",
                    ["ItemBlocked"] = "Item blocked: {0} (Skin: {1})",
                    ["SkinRemoved"] = "Skin removed from item: {0}",
                    ["NotLookingAtItem"] = "You are not looking at a valid item.",
                },
                this
            );

            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["RestrictedItem"] =
                        "Этот скин на предмете запрещён законом РФ (ФЗ о противодействии экстремистской деятельности)!",
                    ["ItemBlocked"] = "Предмет заблокирован: {0} (Скин: {1})",
                    ["SkinRemoved"] = "Скин удалён с предмета: {0}",
                    ["NotLookingAtItem"] = "Вы не смотрите на предмет.",
                },
                this,
                "ru"
            );
        }

        #endregion

        #region Hooks

        void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player == null || newItem == null)
            {
                StopKillTimer(player);
                return;
            }

            if (IsBlockedSkin(newItem.info.shortname, newItem.skin))
            {
                StartKillTimer(player, newItem);
            }
            else
            {
                StopKillTimer(player);
            }
        }

        void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (container == null || item == null)
                return;

            var player = container.playerOwner;
            if (player == null)
                return;

            if (container == player.inventory.containerWear)
            {
                CheckWornClothing(player);
            }
        }

        void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (container == null || item == null)
                return;

            var player = container.playerOwner;
            if (player == null)
                return;

            if (container == player.inventory.containerWear)
            {
                CheckWornClothing(player);
            }
        }

        void OnPlayerDisconnected(BasePlayer player)
        {
            if (player != null)
            {
                StopKillTimer(player);
                StopClothingTimer(player);
            }
        }

        void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player != null)
            {
                StopKillTimer(player);
                StopClothingTimer(player);
            }
        }

        void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player != null)
            {
                CheckWornClothing(player);
            }
        }

        void OnPlayerConnected(BasePlayer player)
        {
            if (player != null)
            {
                timer.Once(1f, () => CheckWornClothing(player));
            }
        }

        object CanWearItem(PlayerInventory inventory, Item item)
        {
            if (inventory == null || item == null)
                return null;

            var player = inventory.GetComponent<BasePlayer>();
            if (player == null)
                return null;

            if (IsBlockedSkin(item.info.shortname, item.skin))
            {
                ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                PlaySirenSound(player);
                return false;
            }

            return null;
        }

        object CanDeployItem(BasePlayer player, Deployer deployer, NetworkableId entityId)
        {
            if (player == null)
                return null;

            Item item = player.GetActiveItem();
            if (item == null)
                return null;

            if (IsBlockedSkin(item.info.shortname, item.skin))
            {
                ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                PlaySirenSound(player);
                return false;
            }

            return null;
        }

        object OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null)
                return null;

            if (!input.WasJustPressed(BUTTON.FIRE_PRIMARY))
                return null;

            Item activeItem = player.GetActiveItem();
            if (activeItem == null)
                return null;

            if (IsBlockedSkin(activeItem.info.shortname, activeItem.skin))
            {
                ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                PlaySirenSound(player);
                return false;
            }

            return null;
        }

        void OnItemDeployed(Deployer deployer, BaseEntity deployedEntity)
        {
            if (deployedEntity == null)
                return;

            BasePlayer player = deployer?.GetOwnerPlayer();
            if (player == null && deployedEntity.OwnerID != 0)
            {
                player = BasePlayer.FindByID(deployedEntity.OwnerID);
            }

            if (player == null)
                return;

            ulong entitySkin = deployedEntity.skinID;
            if (entitySkin == 0)
                return;

            string prefabName = deployedEntity.ShortPrefabName ?? deployedEntity.PrefabName ?? "";
            ItemDefinition itemDef = ItemManager.FindItemDefinition(prefabName);
            if (itemDef == null)
                return;

            if (IsBlockedSkin(itemDef.shortname, entitySkin))
            {
                deployedEntity.Kill();
                ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                PlaySirenSound(player);
            }
        }

        #endregion

        #region Core Logic

        private bool IsBlockedSkin(string shortname, ulong skin)
        {
            if (string.IsNullOrEmpty(shortname) || skin == 0)
                return false;

            if (configData.BlockedSkins.TryGetValue(shortname, out var blockedSkins))
            {
                return blockedSkins.Contains(skin);
            }

            return false;
        }

        private void StartKillTimer(BasePlayer player, Item item)
        {
            if (player == null || !player.IsConnected || player.IsDead())
                return;

            StopKillTimer(player);

            ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
            PlaySirenSound(player);

            activeKillTimers[player.userID] = timer.Every(
                configData.DamageInterval,
                () =>
                {
                    if (player == null || !player.IsConnected || player.IsDead())
                    {
                        StopKillTimer(player);
                        return;
                    }

                    var activeItem = player.GetActiveItem();
                    if (
                        activeItem == null
                        || !IsBlockedSkin(activeItem.info.shortname, activeItem.skin)
                    )
                    {
                        StopKillTimer(player);
                        return;
                    }

                    player.Hurt(configData.DamageAmount, Rust.DamageType.Generic, null, false);
                    ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                    PlaySirenSound(player);
                }
            );
        }

        private void StopKillTimer(BasePlayer player)
        {
            if (player == null)
                return;

            if (activeKillTimers.TryGetValue(player.userID, out var timer))
            {
                timer?.Destroy();
                activeKillTimers.Remove(player.userID);
            }
        }

        private void CheckWornClothing(BasePlayer player)
        {
            if (player == null || !player.IsConnected || player.IsDead())
            {
                StopClothingTimer(player);
                return;
            }

            bool hasBlockedClothing = false;

            foreach (var item in player.inventory.containerWear.itemList)
            {
                if (item != null && IsBlockedSkin(item.info.shortname, item.skin))
                {
                    hasBlockedClothing = true;
                    break;
                }
            }

            if (hasBlockedClothing)
            {
                StartClothingTimer(player);
            }
            else
            {
                StopClothingTimer(player);
            }
        }

        private void StartClothingTimer(BasePlayer player)
        {
            if (player == null || !player.IsConnected || player.IsDead())
                return;

            if (activeClothingTimers.ContainsKey(player.userID))
                return;

            ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
            PlaySirenSound(player);

            activeClothingTimers[player.userID] = timer.Every(
                configData.DamageInterval,
                () =>
                {
                    if (player == null || !player.IsConnected || player.IsDead())
                    {
                        StopClothingTimer(player);
                        return;
                    }

                    bool hasBlockedClothing = false;
                    foreach (var item in player.inventory.containerWear.itemList)
                    {
                        if (item != null && IsBlockedSkin(item.info.shortname, item.skin))
                        {
                            hasBlockedClothing = true;
                            break;
                        }
                    }

                    if (!hasBlockedClothing)
                    {
                        StopClothingTimer(player);
                        return;
                    }

                    player.Hurt(configData.DamageAmount, Rust.DamageType.Generic, null, false);
                    ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                    PlaySirenSound(player);
                }
            );
        }

        private void StopClothingTimer(BasePlayer player)
        {
            if (player == null)
                return;

            if (activeClothingTimers.TryGetValue(player.userID, out var timer))
            {
                timer?.Destroy();
                activeClothingTimers.Remove(player.userID);
            }
        }

        private void ShowGameTip(BasePlayer player, string message)
        {
            if (player?.IsConnected != true)
            {
                return;
            }

            player.SendConsoleCommand("gametip.showtoast", 0, message);
        }

        private void PlaySirenSound(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            if (!string.IsNullOrEmpty(configData.SirenSound))
            {
                Effect effect = new Effect(
                    configData.SirenSound,
                    player,
                    0,
                    Vector3.zero,
                    Vector3.forward
                );
                EffectNetwork.Send(effect, player.Connection);
            }
        }

        #endregion

        #region Commands

        [ChatCommand("ic")]
        private void BlockItemCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin)
                return;

            string shortname = null;
            ulong skin = 0;
            bool isDeployed = false;
            Item targetItem = null;
            BaseEntity targetEntity = null;

            var activeItem = player.GetActiveItem();
            if (activeItem != null && activeItem.skin != 0)
            {
                targetItem = activeItem;
                shortname = activeItem.info.shortname;
                skin = activeItem.skin;
            }
            else
            {
                targetItem = GetTargetItem(player);
                if (targetItem != null)
                {
                    shortname = targetItem.info.shortname;
                    skin = targetItem.skin;
                }
                else
                {
                    targetEntity = GetTargetEntity(player);
                    if (targetEntity != null)
                    {
                        skin = targetEntity.skinID;
                        if (skin == 0)
                        {
                            Print(player, "Этот предмет не имеет скина.");
                            return;
                        }

                        string prefabName =
                            targetEntity.ShortPrefabName ?? targetEntity.PrefabName ?? "";
                        ItemDefinition itemDef = ItemManager.FindItemDefinition(prefabName);
                        if (itemDef == null)
                        {
                            Print(player, Lang("NotLookingAtItem", player.UserIDString));
                            return;
                        }

                        shortname = itemDef.shortname;
                        isDeployed = true;
                    }
                    else
                    {
                        Print(player, Lang("NotLookingAtItem", player.UserIDString));
                        return;
                    }
                }
            }

            if (skin == 0)
            {
                Print(player, "Этот предмет не имеет скина.");
                return;
            }

            if (!configData.BlockedSkins.ContainsKey(shortname))
            {
                configData.BlockedSkins[shortname] = new List<ulong>();
            }

            if (!configData.BlockedSkins[shortname].Contains(skin))
            {
                configData.BlockedSkins[shortname].Add(skin);
                SaveConfig();
            }

            if (isDeployed)
            {
                targetEntity.skinID = 0;
                targetEntity.SendNetworkUpdate();
            }
            else if (targetItem != null)
            {
                targetItem.skin = 0;
                targetItem.MarkDirty();

                var heldEntity = targetItem.GetHeldEntity();
                if (heldEntity != null)
                {
                    heldEntity.skinID = 0;
                    heldEntity.SendNetworkUpdate();
                }
            }

            Print(player, Lang("ItemBlocked", player.UserIDString, shortname, skin));
            Print(player, Lang("SkinRemoved", player.UserIDString, shortname));
        }

        private Item GetTargetItem(BasePlayer player)
        {
            if (player == null)
                return null;

            RaycastHit hit;
            if (
                Physics.Raycast(
                    player.eyes.HeadRay(),
                    out hit,
                    5f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore
                )
            )
            {
                BaseEntity entity = hit.GetEntity();
                if (entity is DroppedItem droppedItem && droppedItem.item != null)
                {
                    return droppedItem.item;
                }
            }

            return null;
        }

        private BaseEntity GetTargetEntity(BasePlayer player)
        {
            if (player == null)
                return null;

            RaycastHit hit;
            if (
                Physics.Raycast(
                    player.eyes.HeadRay(),
                    out hit,
                    5f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore
                )
            )
            {
                BaseEntity entity = hit.GetEntity();
                if (entity != null && entity.skinID != 0)
                {
                    return entity;
                }
            }

            return null;
        }

        #endregion
    }
}
