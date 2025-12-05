//1.0.1 Добавлена блокировка одевания одежды
//1.0.2 Добавлена локализация для сообщения об отсутствии скина, добавлена проверка изменения скина ентити через баллончик с разрушением ентити и нанесением урона игроку (20 единиц)
//1.0.3 Плагин переработан с 100% соответствием правилам Facepunch. Все механизмы используют чисто игровую механику без блокировок и упоминаний законов.
//1.0.4 Добавлена команда ric add2 с автоматическим определением шортнеймов через Steam API и добавлением скинов в конфигурацию.
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RItemCompliance", "RobinPlay", "1.0.4")]
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

            [JsonProperty("Steam API Key")]
            public string SteamApiKey { get; set; } = "";
        }

        protected override void LoadDefaultConfig()
        {
            configData = new ConfigData();
            configData.BlockedSkins["sleepingbag"] = new List<ulong> { 2921636205, 3002450787 };
            configData.BlockedSkins["door.hinged.toptier"] = new List<ulong>
            {
                2799741628,
                2911337044,
                2885575403,
                2830140703,
                1999927543,
                1376526519,
            };
            configData.BlockedSkins["wall.frame.garagedoor"] = new List<ulong>
            {
                3049318629,
                2936423837,
                2918938368,
                2366648978,
                2570889432,
                2483380380,
                2222230165,
            };
            configData.BlockedSkins["door.hinged.metal"] = new List<ulong>
            {
                3568372096,
                3488341034,
                3345036303,
                3048905728,
                2949621111,
                2811788926,
                2217374019,
                2431554472,
                2396828738,
                2091097349,
                1952448742,
                1653322594,
            };
            configData.BlockedSkins["door.double.hinged.wood"] = new List<ulong> { 3334342910 };
            configData.BlockedSkins["door.double.hinged.metal"] = new List<ulong>
            {
                3581911522,
                3496304344,
                3275117030,
                1882782756,
            };
            configData.BlockedSkins["box.wooden.large"] = new List<ulong>
            {
                1196352289,
                1900496901,
                2156384652,
                2447526502,
            };
            configData.BlockedSkins["box.wooden"] = new List<ulong> { 2394650621 };
            configData.BlockedSkins["rug"] = new List<ulong> { 2875313479 };
            configData.BlockedSkins["locker"] = new List<ulong> { 3357525274, 2960258956 };
            configData.BlockedSkins["metal.plate.torso"] = new List<ulong> { 3559835047 };
            configData.BlockedSkins["hoodie"] = new List<ulong> { 3040981952 };
            configData.BlockedSkins["attire.hide.pants"] = new List<ulong> { 3135159428 };
            configData.BlockedSkins["explosive.satchel"] = new List<ulong> { 2894489438 };
            configData.SteamApiKey = "";
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

        private Dictionary<string, string> _workshopNameToShortname = new Dictionary<string, string>
        {
            ["bandana"] = "mask.bandana",
            ["balaclava"] = "mask.balaclava",
            ["beeniehat"] = "hat.beenie",
            ["burlapshoes"] = "burlap.shoes",
            ["burlapshirt"] = "burlap.shirt",
            ["burlappants"] = "burlap.trousers",
            ["burlapheadwrap"] = "burlap.headwrap",
            ["buckethelmet"] = "bucket.helmet",
            ["booniehat"] = "hat.boonie",
            ["cap"] = "hat.cap",
            ["collaredshirt"] = "shirt.collared",
            ["coffeecanhelmet"] = "coffeecan.helmet",
            ["deerskullmask"] = "deer.skull.mask",
            ["hideskirt"] = "attire.hide.skirt",
            ["hideshirt"] = "attire.hide.vest",
            ["hidepants"] = "attire.hide.pants",
            ["hideshoes"] = "attire.hide.boots",
            ["hidehalterneck"] = "attire.hide.helterneck",
            ["hoodie"] = "hoodie",
            ["hideponcho"] = "attire.hide.poncho",
            ["leathergloves"] = "burlap.gloves",
            ["longtshirt"] = "tshirt.long",
            ["metalchestplate"] = "metal.plate.torso",
            ["metalfacemask"] = "metal.facemask",
            ["minerhat"] = "hat.miner",
            ["pants"] = "pants",
            ["roadsignvest"] = "roadsign.jacket",
            ["roadsignpants"] = "roadsign.kilt",
            ["riothelmet"] = "riot.helmet",
            ["snowjacket"] = "jacket.snow",
            ["shorts"] = "pants.shorts",
            ["tanktop"] = "shirt.tanktop",
            ["tshirt"] = "tshirt",
            ["vagabondjacket"] = "jacket",
            ["workboots"] = "shoes.boots",
            ["ak47"] = "rifle.ak",
            ["boltrifle"] = "rifle.bolt",
            ["boneclub"] = "bone.club",
            ["boneknife"] = "knife.bone",
            ["crossbow"] = "crossbow",
            ["doublebarrelshotgun"] = "shotgun.double",
            ["eokapistol"] = "pistol.eoka",
            ["f1grenade"] = "grenade.f1",
            ["longsword"] = "longsword",
            ["mp5"] = "smg.mp5",
            ["pumpshotgun"] = "shotgun.pump",
            ["rock"] = "rock",
            ["salvagedhammer"] = "hammer.salvaged",
            ["salvagedicepick"] = "icepick.salvaged",
            ["satchelcharge"] = "explosive.satchel",
            ["semiautomaticpistol"] = "pistol.semiauto",
            ["stonehatchet"] = "stonehatchet",
            ["stonepickaxe"] = "stone.pickaxe",
            ["largewoodbox"] = "box.wooden.large",
            ["reactivetarget"] = "target.reactive",
            ["sandbagbarricade"] = "barricade.sandbags",
            ["sleepingbag"] = "sleepingbag",
            ["sheetmetaldoor"] = "door.hinged.metal",
            ["waterpurifier"] = "water.purifier",
            ["woodstoragebox"] = "box.wooden",
            ["woodendoor"] = "door.hinged.wood",
            ["acousticguitar"] = "fun.guitar",
            ["pickaxe"] = "pickaxe",
            ["hatchet"] = "hatchet",
            ["revolver"] = "pistol.revolver",
            ["rocketlauncher"] = "rocket.launcher",
            ["semiautomaticrifle"] = "rifle.semiauto",
            ["waterpipeshotgun"] = "shotgun.waterpipe",
            ["customsmg"] = "smg.2",
            ["python"] = "pistol.python",
            ["lr300"] = "rifle.lr300",
            ["combatknife"] = "knife.combat",
            ["armoreddoor"] = "door.hinged.toptier",
            ["concretebarricade"] = "barricade.concrete",
            ["thompson"] = "smg.thompson",
            ["hammer"] = "hammer",
            ["sword"] = "salvaged.sword",
            ["huntingbow"] = "bow.hunting",
            ["m249"] = "lmg.m249",
            ["m39"] = "rifle.m39",
            ["l96"] = "rifle.l96",
            ["locker"] = "locker",
            ["vendingmachine"] = "vending.machine",
            ["fridge"] = "fridge",
            ["garagedoor"] = "wall.frame.garagedoor",
            ["armoreddoubledoor"] = "door.double.hinged.toptier",
            ["sheetmetaldoubledoor"] = "door.double.hinged.metal",
            ["woodendoubledoor"] = "door.double.hinged.wood",
            ["furnace"] = "furnace",
            ["jackhammer"] = "jackhammer",
            ["table"] = "table",
            ["roadsigngloves"] = "roadsign.gloves",
            ["bearrug"] = "rug.bear",
            ["rug"] = "rug",
            ["chair"] = "chair",
            ["spinningwheel"] = "spinner.wheel",
            ["largebackpack"] = "largebackpack",
            ["wallpaperwall"] = "wallpaper.wall",
            ["wallpaperflooring"] = "wallpaper.flooring",
            ["wallpaperceiling"] = "wallpaper.ceiling",
            ["bed"] = "bed",
            ["hmlmg"] = "hmlmg",
        };

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
                        "This item is corrupted by dark forces and causes harm to the player!",
                    ["ItemBlocked"] = "Item blocked: {0} (Skin: {1})",
                    ["SkinRemoved"] = "Skin removed from item: {0}",
                    ["NotLookingAtItem"] = "You are not looking at a valid item.",
                    ["NoSkin"] = "This item has no skin.",
                },
                this
            );

            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["RestrictedItem"] =
                        "Этот предмет осквернён тёмными силами и наносит вред игроку!",
                    ["ItemBlocked"] = "Предмет заблокирован: {0} (Скин: {1})",
                    ["SkinRemoved"] = "Скин удалён с предмета: {0}",
                    ["NotLookingAtItem"] = "Вы не смотрите на предмет.",
                    ["NoSkin"] = "Этот предмет не имеет скина.",
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
                if (newItem.info.shortname == "explosive.satchel")
                {
                    ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                    PlaySirenSound(player);
                    player.Hurt(configData.DamageAmount, Rust.DamageType.Generic, null, false);

                    NextTick(() =>
                    {
                        if (player == null || !player.IsConnected)
                            return;

                        var activeItem = player.GetActiveItem();
                        if (activeItem == null || activeItem.uid != newItem.uid)
                            return;

                        if (IsBlockedSkin(activeItem.info.shortname, activeItem.skin))
                        {
                            RemoveActiveItem(player, activeItem);
                        }
                    });
                }
                else
                {
                    StartKillTimer(player, newItem);
                }
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
                if (IsBlockedSkin(item.info.shortname, item.skin))
                {
                    ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                    PlaySirenSound(player);

                    timer.Once(
                        0.1f,
                        () =>
                        {
                            if (item != null && item.parent == container && item.hasCondition)
                            {
                                item.LoseCondition(item.maxCondition);
                            }
                        }
                    );
                }

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
                if (item.info.shortname == "explosive.satchel")
                {
                    return null;
                }
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
                if (activeItem.info.shortname == "explosive.satchel")
                {
                    return null;
                }
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

            timer.Once(
                0.3f,
                () =>
                {
                    if (deployedEntity == null || deployedEntity.IsDestroyed)
                        return;

                    ulong entitySkin = deployedEntity.skinID;
                    if (entitySkin == 0)
                        return;

                    string prefabName =
                        deployedEntity.ShortPrefabName ?? deployedEntity.PrefabName ?? "";
                    ItemDefinition itemDef = ItemManager.FindItemDefinition(prefabName);

                    if (itemDef == null)
                    {
                        itemDef = GetItemDefinitionByDeployedEntity(deployedEntity);
                    }

                    if (itemDef == null)
                        return;

                    if (IsBlockedSkin(itemDef.shortname, entitySkin))
                    {
                        deployedEntity.Kill();
                        if (player != null && player.IsConnected)
                        {
                            ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                            PlaySirenSound(player);
                        }
                    }
                }
            );
        }

        void OnEntitySpawned(BaseNetworkable networkable)
        {
            if (networkable == null)
                return;

            BaseEntity entity = networkable as BaseEntity;
            if (entity == null)
                return;

            timer.Once(
                0.3f,
                () =>
                {
                    if (entity == null || entity.IsDestroyed)
                        return;

                    ulong entitySkin = entity.skinID;
                    if (entitySkin == 0)
                        return;

                    string prefabName = entity.ShortPrefabName ?? entity.PrefabName ?? "";
                    ItemDefinition itemDef = ItemManager.FindItemDefinition(prefabName);

                    if (itemDef == null)
                    {
                        itemDef = GetItemDefinitionByDeployedEntity(entity);
                    }

                    if (itemDef == null)
                        return;

                    if (IsBlockedSkin(itemDef.shortname, entitySkin))
                    {
                        entity.Kill();
                        BasePlayer player = BasePlayer.FindByID(entity.OwnerID);
                        if (player != null && player.IsConnected)
                        {
                            ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                            PlaySirenSound(player);
                        }
                    }
                }
            );
        }

        void OnEntityReskinned(BaseEntity entity, ItemSkinDirectory.Skin skin, BasePlayer player)
        {
            if (entity == null || player == null)
                return;

            timer.Once(
                0.3f,
                () =>
                {
                    if (entity == null || entity.IsDestroyed)
                        return;

                    string prefabName = entity.ShortPrefabName ?? entity.PrefabName ?? "";
                    ItemDefinition itemDef = ItemManager.FindItemDefinition(prefabName);

                    if (itemDef == null)
                    {
                        itemDef = GetItemDefinitionByDeployedEntity(entity);
                    }

                    if (itemDef == null)
                        return;

                    ulong skinId = entity.skinID;
                    if (IsBlockedSkin(itemDef.shortname, skinId))
                    {
                        entity.Kill();
                        if (player != null && player.IsConnected)
                        {
                            player.Hurt(20f, Rust.DamageType.Generic, null, false);
                            ShowGameTip(player, Lang("RestrictedItem", player.UserIDString));
                            PlaySirenSound(player);
                        }
                    }
                }
            );
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

        private ItemDefinition GetItemDefinitionByDeployedEntity(BaseEntity deployedEntity)
        {
            if (deployedEntity == null)
                return null;

            string entityPrefabPath = deployedEntity.PrefabName;
            if (string.IsNullOrEmpty(entityPrefabPath))
                return null;

            foreach (var itemDef in ItemManager.GetItemDefinitions())
            {
                var itemModDeployable = itemDef.GetComponent<ItemModDeployable>();
                if (itemModDeployable == null)
                    continue;

                if (itemModDeployable.entityPrefab.resourcePath == entityPrefabPath)
                {
                    return itemDef;
                }
            }

            return null;
        }

        private void RemoveActiveItem(BasePlayer player, Item item)
        {
            if (player == null || item == null || !player.IsConnected)
                return;

            var activeItem = player.GetActiveItem();
            if (activeItem == null || activeItem.uid != item.uid)
                return;

            activeItem.RemoveFromContainer();

            if (!activeItem.MoveToContainer(player.inventory.containerMain))
            {
                if (!player.inventory.GiveItem(activeItem))
                {
                    activeItem.Drop(
                        player.eyes.position + player.eyes.HeadForward() * 1.5f,
                        Vector3.zero
                    );
                }
            }

            player.inventory.SendUpdatedInventory(
                PlayerInventory.Type.Main,
                player.inventory.containerMain
            );
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
            if (activeItem != null)
            {
                if (activeItem.skin == 0)
                {
                    Print(player, Lang("NoSkin", player.UserIDString));
                    return;
                }

                targetItem = activeItem;
                shortname = activeItem.info.shortname;
                skin = activeItem.skin;
            }
            else
            {
                targetItem = GetTargetItem(player);
                if (targetItem != null)
                {
                    if (targetItem.skin == 0)
                    {
                        Print(player, Lang("NoSkin", player.UserIDString));
                        return;
                    }

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
                            Print(player, Lang("NoSkin", player.UserIDString));
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
                Print(player, Lang("NoSkin", player.UserIDString));
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

        #region Steam API Classes

        public class PublishedFileDetails
        {
            public string publishedfileid;
            public string title;
            public Tag[] tags;

            public class Tag
            {
                public string tag;
            }
        }

        public class SkinsResponse
        {
            public PublishedFileDetails[] publishedfiledetails;
        }

        public class SkinsQueryResponse
        {
            public SkinsResponse response;
        }

        #endregion

        #region Console Commands

        [ConsoleCommand("ric")]
        private void ConsoleCommandAddSkins(ConsoleSystem.Arg args)
        {
            if (args?.Args == null || args.Args.Length < 2)
                return;

            if (args.Player() != null)
                return;

            if (args.Args[0] != "add2")
                return;

            if (string.IsNullOrEmpty(configData.SteamApiKey))
            {
                Puts("Steam API Key не настроен в конфигурации!");
                return;
            }

            List<ulong> skinIDs = new List<ulong>();
            int maxSkins = 15;

            for (int i = 1; i < args.Args.Length && skinIDs.Count < maxSkins; i++)
            {
                if (ulong.TryParse(args.Args[i], out ulong skinID))
                {
                    skinIDs.Add(skinID);
                }
                else
                {
                    Puts($"Неверный SkinID: {args.Args[i]}");
                }
            }

            if (skinIDs.Count == 0)
            {
                Puts("Не найдено ни одного валидного SkinID!");
                return;
            }

            string details = $"?key={configData.SteamApiKey}&itemcount={skinIDs.Count}";

            for (int i = 0; i < skinIDs.Count; i++)
            {
                details += $"&publishedfileids[{i}]={skinIDs[i]}";
            }

            webrequest.Enqueue(
                "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
                details,
                (code, response) => OnSkinsRequestComplete(code, response, skinIDs.Count),
                this,
                RequestMethod.POST
            );
        }

        private void OnSkinsRequestComplete(int code, string response, int countAll)
        {
            if (code != 200 || string.IsNullOrEmpty(response))
            {
                Puts("Ошибка при запросе к Steam API!");
                return;
            }

            try
            {
                SkinsQueryResponse sQR = JsonConvert.DeserializeObject<SkinsQueryResponse>(
                    response
                );

                if (
                    sQR?.response == null
                    || sQR.response.publishedfiledetails == null
                    || sQR.response.publishedfiledetails.Length == 0
                )
                {
                    Puts("Ошибка при добавлении скинов: неверный ответ от Steam API");
                    return;
                }

                int count = ParseAndAddSkins(sQR);

                Puts($"Успешно добавлено {count}/{countAll} скинов.");
                SaveConfig();
            }
            catch (System.Exception ex)
            {
                Puts($"Ошибка при обработке ответа Steam API: {ex.Message}");
            }
        }

        private int ParseAndAddSkins(SkinsQueryResponse sQR)
        {
            int count = 0;

            foreach (PublishedFileDetails publishedFileDetails in sQR.response.publishedfiledetails)
            {
                if (publishedFileDetails.tags == null)
                    continue;

                foreach (PublishedFileDetails.Tag tag in publishedFileDetails.tags)
                {
                    string normalizedTag = tag
                        .tag.ToLower()
                        .Replace("skin", "")
                        .Replace(" ", "")
                        .Replace("-", "")
                        .Replace(".item", "");

                    if (string.IsNullOrEmpty(normalizedTag))
                        continue;

                    if (!_workshopNameToShortname.TryGetValue(normalizedTag, out string shortname))
                        continue;

                    if (!ulong.TryParse(publishedFileDetails.publishedfileid, out ulong skinID))
                        continue;

                    if (!configData.BlockedSkins.ContainsKey(shortname))
                    {
                        configData.BlockedSkins[shortname] = new List<ulong>();
                    }

                    if (!configData.BlockedSkins[shortname].Contains(skinID))
                    {
                        configData.BlockedSkins[shortname].Add(skinID);
                        count++;
                    }
                }
            }

            return count;
        }

        #endregion
    }
}
