using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RSurvivalStore", "RustInnovate", "2.9.7")]
    [Description(
        "Клиент RSurvivalStore с пользовательским интерфейсом на основе изображений, ScrollView и WipeBlock."
    )]
    public class RSurvivalStore : RustPlugin
    {
        private Configuration _config;
        private Dictionary<string, string> _imageCache = new Dictionary<string, string>();
        private Dictionary<string, string> _slotStates = new Dictionary<string, string>();
        private Dictionary<string, JArray> _playerItems = new Dictionary<string, JArray>();
        private Dictionary<ulong, string> _activeAddonParent = new Dictionary<ulong, string>();
        private bool _isRegistered = false;
        private string _hudIconPngId = "";
        private string _blueprintIconPngId = "";

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Настройка магазина SurvivalShop")]
            public ShopConfig Settings { get; set; } = new ShopConfig();

            [JsonProperty("Настройка блокировки товаров после вайпа")]
            public List<WipeBlockItem> WipeBlocks { get; set; } = new List<WipeBlockItem>();

            [JsonProperty("Настройки интерфейса")]
            public UISettingsConfig UI { get; set; } = new UISettingsConfig();
        }

        private class ShopConfig
        {
            [JsonProperty("SiteID")]
            public string SiteID { get; set; } = "";

            [JsonProperty("SiteKey")]
            public string SiteKey { get; set; } = "";

            [JsonProperty("Ссылка на магазин (для уведомлений)")]
            public string ShopURL { get; set; } = "https://myshop.survivalshop.org";

            [JsonProperty("Запретить открывать корзину магазина в зоне чужого шкафа?")]
            public bool BlockInEnemyTC { get; set; } = false;

            [JsonProperty("Запретить открывать корзину магазина во время рейдблока?")]
            public bool BlockInRaid { get; set; } = true;

            [JsonProperty("Запретить открывать корзину магазина во время комбатблока?")]
            public bool BlockInCombat { get; set; } = true;
        }

        private class WipeBlockItem
        {
            [JsonProperty("Имя предмета (опционально)")]
            public string Name { get; set; } = "";

            [JsonProperty("ID товара (ОБЯЗАТЕЛЬНО)")]
            public string ItemId { get; set; } = "";

            [JsonProperty("Время блокировки (в часах) (ОБЯЗАТЕЛЬНО)")]
            public float BlockHours { get; set; } = 24f;
        }

        private class UISettingsConfig
        {
            [JsonProperty("1. Общий фон меню в автономном режиме (цвет)")]
            public string MainBgColor { get; set; } = "0 0 0 0.5";

            #region Panels (Header & Content)
            [JsonProperty("2. Смещение панели при встраивании в меню (RServerMenu)")]
            public float EmbedOffsetX { get; set; } = 103.0f;

            [JsonProperty("3. Смещение панели в автономном режиме (/store)")]
            public float StandaloneOffsetX { get; set; } = 0.0f;

            [JsonProperty("4. Верхняя панель (Заголовок)")]
            public HeaderPanelConfig HeaderPanel { get; set; } = new HeaderPanelConfig();

            [JsonProperty("5. Кнопка 'Забрать всё' в шапке")]
            public TakeAllButtonConfig TakeAllButton { get; set; } = new TakeAllButtonConfig();

            [JsonProperty("6. Нижняя панель (Контент)")]
            public ContentPanelConfig ContentPanel { get; set; } = new ContentPanelConfig();

            [JsonProperty("7. Сетка корзины (Карточки товаров)")]
            public CartGridConfig CartGrid { get; set; } = new CartGridConfig();

            [JsonProperty("8. Иконка корзины на экране (HUD)")]
            public CartHudConfig CartHud { get; set; } = new CartHudConfig();
            #endregion
        }

        private class HeaderPanelConfig
        {
            [JsonProperty("Высота")]
            public float Height { get; set; } = 50.0f;

            [JsonProperty("Ширина")]
            public float Width { get; set; } = 699.0f;

            [JsonProperty("Вверх/вниз")]
            public float OffsetY { get; set; } = 226.5f;

            [JsonProperty("Цвет фона")]
            public string BgColor { get; set; } = "0.45 0.46 0.46 0.78";

            [JsonProperty("Цвет текста заголовка")]
            public string TitleColor { get; set; } = "0.25 0.69 1 1";

            [JsonProperty("Размер шрифта заголовка")]
            public int TitleSize { get; set; } = 20;

            [JsonProperty("Отступ заголовка слева")]
            public float TitlePaddingLeft { get; set; } = 15.0f;

            [JsonProperty("Отступ заголовка справа")]
            public float TitlePaddingRight { get; set; } = 170.0f;
        }

        private class TakeAllButtonConfig
        {
            [JsonProperty("Высота")]
            public float Height { get; set; } = 30.0f;

            [JsonProperty("Ширина")]
            public float Width { get; set; } = 140.0f;

            [JsonProperty("Вверх/вниз")]
            public float OffsetY { get; set; } = 0.0f;

            [JsonProperty("Влево/вправо")]
            public float OffsetX { get; set; } = -10.0f;

            [JsonProperty("Цвет фона")]
            public string BgColor { get; set; } = "0.76 0.43 0.20 0.95";

            [JsonProperty("Цвет текста")]
            public string TextColor { get; set; } = "1 1 1 1";

            [JsonProperty("Размер шрифта")]
            public int FontSize { get; set; } = 13;
        }

        private class ContentPanelConfig
        {
            [JsonProperty("Высота")]
            public float Height { get; set; } = 444.0f;

            [JsonProperty("Ширина")]
            public float Width { get; set; } = 699.0f;

            [JsonProperty("Вверх/вниз")]
            public float OffsetY { get; set; } = -30.5f;

            [JsonProperty("Цвет фона")]
            public string BgColor { get; set; } = "0.45 0.46 0.46 0.78";
        }

        private class CartGridConfig
        {
            [JsonProperty("Сетка - Внутренний отступ")]
            public float Padding { get; set; } = 10.0f;

            [JsonProperty("Количество колонок")]
            public int Columns { get; set; } = 5;

            [JsonProperty("Карточка - Ширина")]
            public float CardWidth { get; set; } = 126.0f;

            [JsonProperty("Карточка - Высота")]
            public float CardHeight { get; set; } = 135.0f;

            [JsonProperty("Карточка - Отступ по горизонтали (X)")]
            public float GapX { get; set; } = 8.0f;

            [JsonProperty("Карточка - Отступ по вертикали (Y)")]
            public float GapY { get; set; } = 8.0f;

            [JsonProperty("Карточка (Обычная) - Цвет фона")]
            public string CardBgColor { get; set; } = "0.18 0.20 0.20 0.90";

            [JsonProperty("Карточка (Чертеж) - URL изображения чертежа")]
            public string BlueprintIconUrl { get; set; } =
                "https://pic.survivalhost.org/images/2026/08/25/blueprint.png";

            [JsonProperty("Карточка (Успешно взято) - Цвет фона")]
            public string SuccessBgColor { get; set; } = "0.15 0.35 0.15 0.92";

            [JsonProperty("Карточка (Заблокировано) - Цвет фона")]
            public string BlockedBgColor { get; set; } = "0.35 0.15 0.15 0.92";

            [JsonProperty("Карточка - Цвет текста названия")]
            public string TitleColor { get; set; } = "1 1 1 1";

            [JsonProperty("Карточка - Размер шрифта названия")]
            public int TitleFontSize { get; set; } = 11;

            [JsonProperty("Карточка - Цвет текста количества")]
            public string CountColor { get; set; } = "0.63 0.89 0.18 1.0";

            [JsonProperty("Карточка - Размер шрифта количества")]
            public int CountFontSize { get; set; } = 12;

            [JsonProperty("Иконка предмета - Размер")]
            public float ImageSize { get; set; } = 80.0f;

            [JsonProperty("Иконка предмета - Смещение по Y внутри карточки")]
            public float ImageOffsetY { get; set; } = 0.0f;
        }

        private class CartHudConfig
        {
            [JsonProperty("Включить иконку HUD")]
            public bool Enabled { get; set; } = true;

            [JsonProperty("URL изображения")]
            public string IconUrl { get; set; } =
                "https://pic.survivalhost.org/images/2026/08/25/store.png";

            [JsonProperty("Высота")]
            public float Height { get; set; } = 40.0f;

            [JsonProperty("Ширина")]
            public float Width { get; set; } = 40.0f;

            [JsonProperty("Вверх/вниз")]
            public float OffsetY { get; set; } = 340.0f;

            [JsonProperty("Влево/вправо")]
            public float OffsetX { get; set; } = -615.0f;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                    throw new Exception();
            }
            catch
            {
                Config.WriteObject(_config, false, $"{Config.Filename}.Error.txt");
                PrintError(
                    "The configuration file contains an error and has been replaced with a default config."
                );
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            _config.WipeBlocks.Add(
                new WipeBlockItem
                {
                    Name = "",
                    ItemId = "",
                    BlockHours = 24f,
                }
            );
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Hooks & Setup

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyPlayerUi(player);
                DestroyHUD(player);
            }
        }

        private void OnServerInitialized(bool serverInitialized = false)
        {
            Puts($"Загрузка плагина RSurvivalStore v{Version}");
            Puts("==================================================");
            Puts("          Plugin by RustInnovate                  ");
            Puts("--------------------------------------------------");
            Puts("  VK: vk.com/rustinnovate                         ");
            Puts("  Discord: discord.gg/e244z6aGs7                  ");
            Puts("  Telegram: t.me/RobinPlay                        ");
            Puts("==================================================");

            // CHANGE: Загрузка иконки HUD и изображения чертежа по URL
            DownloadHudIcon();
            DownloadBlueprintIcon();

            if (
                string.IsNullOrEmpty(_config.Settings.SiteID)
                || string.IsNullOrEmpty(_config.Settings.SiteKey)
            )
            {
                PrintWarning(
                    "[RUS] Плагин не настроен. Пожалуйста, используйте 'rsurvivalstore.setup <SiteID> <SiteKey>' в RCON.\n"
                        + "[EN] Plugin is not configured. Please use 'rsurvivalstore.setup <SiteID> <SiteKey>' в RCON."
                );
                return;
            }

            RegisterServer();
            timer.Every(60f, RegisterServer); // Send heartbeat every 60 seconds

            foreach (var player in BasePlayer.activePlayerList)
            {
                DrawHUD(player);
            }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            DrawHUD(player);
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == "RServerMenu")
            {
                foreach (var player in BasePlayer.activePlayerList)
                    DestroyHUD(player);
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin.Name == "RServerMenu")
            {
                timer.Once(
                    0.1f,
                    () =>
                    {
                        foreach (var player in BasePlayer.activePlayerList)
                            DrawHUD(player);
                    }
                );
            }
        }

        // CHANGE: Метод загрузки и кэширования иконки корзины HUD по URL
        private void DownloadHudIcon()
        {
            var hudCfg = _config?.UI?.CartHud;
            if (hudCfg == null || !hudCfg.Enabled || string.IsNullOrEmpty(hudCfg.IconUrl))
                return;

            string cacheDir = Path.Combine(
                Interface.Oxide.DataDirectory,
                "RSystem",
                "RSurvivalStore",
                "ImageCache"
            );
            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            string fileName = "hud_cart_" + hudCfg.IconUrl.GetHashCode().ToString() + ".png";
            string localPath = Path.Combine(cacheDir, fileName);

            if (File.Exists(localPath))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(localPath);
                    uint id = FileStorage.server.Store(
                        data,
                        FileStorage.Type.png,
                        CommunityEntity.ServerInstance.net.ID
                    );
                    _hudIconPngId = id.ToString();
                    return;
                }
                catch (Exception ex)
                {
                    PrintError(
                        $"[RSurvivalStore] Ошибка загрузки закэшированной иконки HUD: {ex.Message}"
                    );
                }
            }

            ServerMgr.Instance.StartCoroutine(
                DownloadImageCoroutine(
                    hudCfg.IconUrl,
                    localPath,
                    "hud_cart",
                    () =>
                    {
                        if (_imageCache.TryGetValue("hud_cart", out string pngId))
                        {
                            _hudIconPngId = pngId;
                            foreach (var p in BasePlayer.activePlayerList)
                            {
                                DrawHUD(p);
                            }
                        }
                    }
                )
            );
        }

        // CHANGE: Метод загрузки и кэширования изображения чертежа по URL
        private void DownloadBlueprintIcon()
        {
            var gridCfg = _config?.UI?.CartGrid;
            if (gridCfg == null || string.IsNullOrEmpty(gridCfg.BlueprintIconUrl))
                return;

            string cacheDir = Path.Combine(
                Interface.Oxide.DataDirectory,
                "RSystem",
                "RSurvivalStore",
                "ImageCache"
            );
            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            string fileName = "bp_" + gridCfg.BlueprintIconUrl.GetHashCode().ToString() + ".png";
            string localPath = Path.Combine(cacheDir, fileName);

            if (File.Exists(localPath))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(localPath);
                    uint id = FileStorage.server.Store(
                        data,
                        FileStorage.Type.png,
                        CommunityEntity.ServerInstance.net.ID
                    );
                    _blueprintIconPngId = id.ToString();
                    return;
                }
                catch (Exception ex)
                {
                    PrintError(
                        $"[RSurvivalStore] Ошибка загрузки закэшированной иконки чертежа: {ex.Message}"
                    );
                }
            }

            ServerMgr.Instance.StartCoroutine(
                DownloadImageCoroutine(
                    gridCfg.BlueprintIconUrl,
                    localPath,
                    "blueprint_icon",
                    () =>
                    {
                        if (_imageCache.TryGetValue("blueprint_icon", out string pngId))
                        {
                            _blueprintIconPngId = pngId;
                        }
                    }
                )
            );
        }

        [ConsoleCommand("rsurvivalstore.setup")]
        private void CmdSetup(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
                return;

            if (!arg.HasArgs(2))
            {
                Puts("Usage: rsurvivalstore.setup <SiteID> <SiteKey>");
                return;
            }

            _config.Settings.SiteID = arg.GetString(0);
            _config.Settings.SiteKey = arg.GetString(1);
            SaveConfig();

            Puts("RSurvivalStore setup complete! Registering server...");
            RegisterServer();
        }

        #endregion

        #region API Interaction

        private void SendApiRequest(
            string endpoint,
            Dictionary<string, object> data,
            Action<int, string> callback
        )
        {
            if (
                string.IsNullOrEmpty(_config.Settings.SiteID)
                || string.IsNullOrEmpty(_config.Settings.SiteKey)
            )
                return;

            string dataJson = JsonConvert.SerializeObject(data, Formatting.None);
            string salt = "salt";
            string signature;

            using (var sha256 = new SHA256Managed())
            {
                byte[] bytes = sha256.ComputeHash(
                    Encoding.UTF8.GetBytes(dataJson + _config.Settings.SiteKey + salt)
                );
                StringBuilder hex = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                    hex.AppendFormat("{0:x2}", b);
                signature = $"{hex}:{_config.Settings.SiteID}:{salt}";
            }

            data["__sign"] = signature;
            string finalJson = JsonConvert.SerializeObject(data, Formatting.None);

            webrequest.Enqueue(
                $"https://api.survivalshop.org/{endpoint}",
                finalJson,
                (code, response) =>
                {
                    callback?.Invoke(code, response);
                },
                this,
                RequestMethod.POST,
                new Dictionary<string, string> { { "Content-Type", "application/json" } }
            );
        }

        private void RegisterServer()
        {
            var data = new Dictionary<string, object>
            {
                ["siteId"] = _config.Settings.SiteID,
                ["serverData"] = new Dictionary<string, object>
                {
                    ["game"] = "RUST",
                    ["name"] = ConVar.Server.hostname,
                    ["port"] = ConVar.Server.queryport,
                    ["online"] = BasePlayer.activePlayerList.Count,
                    ["maxplayers"] = ConVar.Server.maxplayers,
                    ["pluginName"] = Name,
                    ["pluginVersion"] = Version.ToString(),
                },
            };

            SendApiRequest(
                "servers.register2",
                data,
                (code, response) =>
                {
                    if (code == 200)
                    {
                        if (!_isRegistered)
                        {
                            Puts("Successfully registered on SurvivalShop.");
                            _isRegistered = true;
                        }
                    }
                    else
                    {
                        if (_isRegistered)
                            Puts($"Lost connection to SurvivalShop or failed to heartbeat: {code}");
                        _isRegistered = false;
                    }
                }
            );
        }

        #endregion

        #region Wipe Block Logic

        private bool IsItemBlocked(string itemId, out float remainingSeconds)
        {
            remainingSeconds = 0f;
            foreach (var block in _config.WipeBlocks)
            {
                if (block.ItemId == itemId)
                {
                    float blockSeconds = block.BlockHours * 3600f;
                    float secondsSinceWipe = (float)
                        (DateTime.Now - SaveRestore.SaveCreatedTime).TotalSeconds;
                    if (secondsSinceWipe < blockSeconds)
                    {
                        remainingSeconds = blockSeconds - secondsSinceWipe;
                        return true;
                    }
                    break; // Item found, but time elapsed
                }
            }
            return false;
        }

        private string FormatTime(float seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return string.Format(
                "{0:D2}ч {1:D2}м {2:D2}с",
                t.Hours + (t.Days * 24),
                t.Minutes,
                t.Seconds
            );
        }

        #endregion

        #region UI & Commands

        private const string UIName = "RSurvivalStoreUI";
        private const string LayerBg = "RSurvivalStore.Bg";
        private const string LayerStatic = "RSurvivalStore.Static";
        private const string LayerHeader = "RSurvivalStore.Header";
        private const string LayerContent = "RSurvivalStore.Content";
        private const string HUDName = "RSurvivalStoreHUD";

        private void DestroyPlayerUi(BasePlayer player)
        {
            if (player == null)
                return;

            _activeAddonParent.Remove(player.userID);
            CuiHelper.DestroyUi(player, UIName);
            CuiHelper.DestroyUi(player, LayerBg);
            CuiHelper.DestroyUi(player, LayerStatic);
            CuiHelper.DestroyUi(player, LayerHeader);
            CuiHelper.DestroyUi(player, LayerContent);
            CuiHelper.DestroyUi(player, UIName + "_TakeAll");
            CuiHelper.DestroyUi(player, UIName + "_Content");
            CuiHelper.DestroyUi(player, UIName + "_Empty");
        }

        private void DrawHUD(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            var rmenu = plugins.Find("RServerMenu");
            if (rmenu != null && rmenu.IsLoaded)
                return;

            var cartCfg = _config?.UI?.CartHud;
            if (cartCfg == null || !cartCfg.Enabled)
                return;

            CuiHelper.DestroyUi(player, HUDName);

            CuiElementContainer container = new CuiElementContainer();

            float halfW = cartCfg.Width / 2f;
            float halfH = cartCfg.Height / 2f;
            float xMin = cartCfg.OffsetX - halfW;
            float xMax = cartCfg.OffsetX + halfW;
            float yMin = cartCfg.OffsetY - halfH;
            float yMax = cartCfg.OffsetY + halfH;

            // CHANGE: Если изображение по ссылке загружено - используем CuiRawImageComponent, иначе fallback кнопку
            if (!string.IsNullOrEmpty(_hudIconPngId))
            {
                container.Add(
                    new CuiElement
                    {
                        Parent = "Hud",
                        Name = HUDName,
                        Components =
                        {
                            new CuiRawImageComponent { Png = _hudIconPngId },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{xMin.ToString(System.Globalization.CultureInfo.InvariantCulture)} {yMin.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                OffsetMax =
                                    $"{xMax.ToString(System.Globalization.CultureInfo.InvariantCulture)} {yMax.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                            },
                        },
                    }
                );
            }
            else
            {
                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = "0.76 0.43 0.20 0.95" },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{xMin.ToString(System.Globalization.CultureInfo.InvariantCulture)} {yMin.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                            OffsetMax =
                                $"{xMax.ToString(System.Globalization.CultureInfo.InvariantCulture)} {yMax.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                        },
                    },
                    "Hud",
                    HUDName
                );

                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = "🛒",
                            FontSize = 18,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1",
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    HUDName
                );
            }

            container.Add(
                new CuiButton
                {
                    Button = { Command = "rsurvivalstore.open", Color = "0 0 0 0" },
                    Text = { Text = "" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                },
                HUDName
            );

            CuiHelper.AddUi(player, container);
        }

        private void DestroyHUD(BasePlayer player)
        {
            if (player == null)
                return;
            CuiHelper.DestroyUi(player, HUDName);
        }

        [ChatCommand("store")]
        private void CmdShopChat(BasePlayer player, string command, string[] args)
        {
            if (_config.Settings.BlockInEnemyTC && player.IsBuildingBlocked())
            {
                ShowGameTip(player, Lang("BlockedInTC", player.UserIDString), 2);
                return;
            }

            if (_config.Settings.BlockInRaid && IsPlayerRaidBlocked(player))
            {
                ShowGameTip(player, Lang("BlockedInRaid", player.UserIDString), 2);
                return;
            }

            if (_config.Settings.BlockInCombat && IsPlayerCombatBlocked(player))
            {
                ShowGameTip(player, Lang("BlockedInCombat", player.UserIDString), 2);
                return;
            }

            var rmenu = plugins.Find("RServerMenu");
            if (rmenu != null && rmenu.IsLoaded)
            {
                rmenu.Call("OpenMenuCategory", player, "cart");
                return;
            }
            _activeAddonParent.Remove(player.userID);
            OpenShopUI(player);
        }

        [HookMethod("DrawEmbedUI_Fixed")]
        public void DrawEmbedUI_Fixed(BasePlayer player, string parentPanel = null)
        {
            OpenAddonUI(player, parentPanel);
        }

        [HookMethod("DrawEmbedUI")]
        public void DrawEmbedUI(BasePlayer player, string parentPanel = null)
        {
            OpenAddonUI(player, parentPanel);
        }

        [HookMethod("OpenAddonUI")]
        public void OpenAddonUI(BasePlayer player, string parentName = null)
        {
            if (player == null || !player.IsConnected)
                return;

            if (_config.Settings.BlockInEnemyTC && player.IsBuildingBlocked())
            {
                ShowGameTip(player, Lang("BlockedInTC", player.UserIDString), 2);
                return;
            }

            if (_config.Settings.BlockInRaid && IsPlayerRaidBlocked(player))
            {
                ShowGameTip(player, Lang("BlockedInRaid", player.UserIDString), 2);
                return;
            }

            if (_config.Settings.BlockInCombat && IsPlayerCombatBlocked(player))
            {
                ShowGameTip(player, Lang("BlockedInCombat", player.UserIDString), 2);
                return;
            }

            _activeAddonParent[player.userID] = !string.IsNullOrEmpty(parentName)
                ? parentName
                : "Overlay";
            OpenShopUI(player);
        }

        // CHANGE: Универсальный хук закрытия для RServerMenu
        [HookMethod("CloseEmbedUI")]
        public void CloseEmbedUI(BasePlayer player)
        {
            if (player == null)
                return;
            DestroyPlayerUi(player);
        }

        [HookMethod("CloseAddonUI")]
        public void CloseAddonUI(BasePlayer player)
        {
            if (player == null)
                return;
            DestroyPlayerUi(player);
        }

        private bool IsPlayerRaidBlocked(BasePlayer player)
        {
            var raidBlock = plugins.Find("RaidBlock");
            if (raidBlock != null && raidBlock.IsLoaded)
            {
                object result = raidBlock.Call("IsBlocked", player);
                if (result is bool && (bool)result)
                    return true;

                result = raidBlock.Call("IsRaidBlocked", player);
                if (result is bool && (bool)result)
                    return true;
            }

            var noEscape = plugins.Find("NoEscape");
            if (noEscape != null && noEscape.IsLoaded)
            {
                object result = noEscape.Call("IsRaidBlocked", player);
                if (result is bool && (bool)result)
                    return true;
            }

            return false;
        }

        private bool IsPlayerCombatBlocked(BasePlayer player)
        {
            var noEscape = plugins.Find("NoEscape");
            if (noEscape != null && noEscape.IsLoaded)
            {
                object result = noEscape.Call("IsCombatBlocked", player);
                if (result is bool && (bool)result)
                    return true;
            }

            var combatBlock = plugins.Find("CombatBlock");
            if (combatBlock != null && combatBlock.IsLoaded)
            {
                object result = combatBlock.Call("IsCombatBlocked", player);
                if (result is bool && (bool)result)
                    return true;

                result = combatBlock.Call("IsCombatBlock", player);
                if (result is bool && (bool)result)
                    return true;
            }

            return false;
        }

        [ConsoleCommand("rsurvivalstore.open")]
        private void CmdOpenUI(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                CmdShopChat(arg.Player(), "", null);
            }
        }

        private void ShowGameTip(BasePlayer player, string message, int type = 2)
        {
            if (player?.IsConnected != true)
                return;
            player.SendConsoleCommand("gametip.showtoast", type, message);
        }

        [ConsoleCommand("rsurvivalstore.close")]
        private void CmdCloseUI(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                DestroyPlayerUi(arg.Player());
            }
        }

        private void OpenShopUI(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            if (!_isRegistered)
            {
                Player.Message(player, Lang("NotConfigured", player.UserIDString));
                return;
            }

            bool isEmbedded = _activeAddonParent.ContainsKey(player.userID);

            // CHANGE: Мгновенная отрисовка каркаса и кэшированных предметов (устраняет мерцание и задержку при переключении категорий)
            JArray cachedItems = null;
            if (_playerItems.TryGetValue(player.UserIDString, out cachedItems))
            {
                DrawUI(player, cachedItems, false);
            }
            else
            {
                DrawUI(player, null, false);
            }

            var data = new Dictionary<string, object>
            {
                ["siteId"] = _config.Settings.SiteID,
                ["clientSid"] = player.UserIDString,
                ["criteria"] = new Dictionary<string, object> { ["_start"] = 0, ["_limit"] = 100 },
            };

            SendApiRequest(
                "client.getInventory",
                data,
                (code, response) =>
                {
                    if (player == null || !player.IsConnected)
                        return;

                    // CHANGE: Если меню было закрыто или переключена вкладка во время ответа API - прерываем отрисовку
                    if (isEmbedded && !_activeAddonParent.ContainsKey(player.userID))
                        return;

                    if (code != 200 || string.IsNullOrEmpty(response))
                    {
                        DrawUI(player, null, true);
                        return;
                    }

                    try
                    {
                        JObject json = JObject.Parse(response);

                        string errorMsg = json["error_msg"]?.ToString();
                        if (errorMsg == null && json["error_code"] != null)
                            errorMsg = "Unknown Error";

                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            if (
                                errorMsg.IndexOf(
                                    "Mesh not exist",
                                    StringComparison.OrdinalIgnoreCase
                                ) >= 0
                                || errorMsg.IndexOf(
                                    "Клиент не найден",
                                    StringComparison.OrdinalIgnoreCase
                                ) >= 0
                            )
                            {
                                DrawUI(player, null, true);
                                return;
                            }

                            Player.Message(player, Lang("ApiError", player.UserIDString));
                            PrintError(
                                $"[RSurvivalStore] API Error for {player.UserIDString}: {errorMsg}"
                            );
                            return;
                        }

                        JArray items = (JArray)json["response"]?["result"];
                        CacheItemImages(
                            player,
                            items,
                            () =>
                            {
                                if (player == null || !player.IsConnected)
                                    return;
                                if (isEmbedded && !_activeAddonParent.ContainsKey(player.userID))
                                    return;

                                // CHANGE: Если список предметов идентичен уже отрисованному кэшу, пропускаем перерисовку для исключения мерцания
                                if (cachedItems != null && JToken.DeepEquals(cachedItems, items))
                                    return;

                                DrawUI(player, items, false);
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        PrintError($"Failed to parse shop data: {ex.Message}");
                    }
                }
            );
        }

        private void CacheItemImages(BasePlayer player, JArray items, Action onComplete)
        {
            if (items == null || items.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }

            string cacheDir = Path.Combine(
                Interface.Oxide.DataDirectory,
                "RSystem",
                "RSurvivalStore",
                "ImageCache"
            );
            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            Dictionary<string, string> pendingDownloads = new Dictionary<string, string>();

            foreach (JObject item in items)
            {
                string logoLink = (string)item["logoLink"];
                if (string.IsNullOrEmpty(logoLink))
                    continue;

                if (_imageCache.ContainsKey(logoLink))
                    continue;

                string imageUrl = logoLink;
                if (logoLink.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    imageUrl = "https://" + logoLink.Substring(7);
                else if (!logoLink.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    imageUrl = "https://survivalshop.org" + logoLink;

                string fileName = logoLink.GetHashCode().ToString() + ".png";
                string localPath = Path.Combine(cacheDir, fileName);

                if (File.Exists(localPath))
                {
                    try
                    {
                        byte[] data = File.ReadAllBytes(localPath);
                        uint id = FileStorage.server.Store(
                            data,
                            FileStorage.Type.png,
                            CommunityEntity.ServerInstance.net.ID
                        );
                        _imageCache[logoLink] = id.ToString();
                    }
                    catch (Exception ex)
                    {
                        PrintError($"Failed to load cached image {fileName}: {ex.Message}");
                        if (!pendingDownloads.ContainsKey(logoLink))
                            pendingDownloads.Add(logoLink, imageUrl);
                    }
                }
                else
                {
                    if (!pendingDownloads.ContainsKey(logoLink))
                        pendingDownloads.Add(logoLink, imageUrl);
                }
            }

            if (pendingDownloads.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }

            int total = pendingDownloads.Count;
            int completed = 0;
            Action itemCompleted = () =>
            {
                completed++;
                if (completed >= total)
                    onComplete?.Invoke();
            };

            foreach (var kvp in pendingDownloads)
            {
                string logoLink = kvp.Key;
                string imageUrl = kvp.Value;
                string fileName = logoLink.GetHashCode().ToString() + ".png";
                string localPath = Path.Combine(cacheDir, fileName);

                ServerMgr.Instance.StartCoroutine(
                    DownloadImageCoroutine(imageUrl, localPath, logoLink, itemCompleted)
                );
            }
        }

        private System.Collections.IEnumerator DownloadImageCoroutine(
            string url,
            string localPath,
            string logoLink,
            Action onComplete
        )
        {
            using (var www = UnityEngine.Networking.UnityWebRequest.Get(url))
            {
                yield return www.SendWebRequest();
                if (www.isNetworkError || www.isHttpError)
                {
                    PrintError($"Failed to download image {url}: {www.error}");
                }
                else
                {
                    byte[] bytes = www.downloadHandler.data;
                    if (bytes != null && bytes.Length > 0)
                    {
                        try
                        {
                            System.IO.File.WriteAllBytes(localPath, bytes);
                            uint id = FileStorage.server.Store(
                                bytes,
                                FileStorage.Type.png,
                                CommunityEntity.ServerInstance.net.ID
                            );
                            _imageCache[logoLink] = id.ToString();
                        }
                        catch (Exception ex)
                        {
                            PrintError($"Failed to save downloaded image {logoLink}: {ex.Message}");
                        }
                    }
                }
                onComplete?.Invoke();
            }
        }

        private bool CheckIsBlueprint(JObject item)
        {
            if (item == null)
                return false;

            string title = (string)item["title"] ?? "";
            if (
                title.IndexOf("чертеж", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("чертёж", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("[ч]", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("[b]", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("[bp]", StringComparison.OrdinalIgnoreCase) >= 0
            )
            {
                return true;
            }

            if (item["is_blueprint"] != null && item["is_blueprint"].Value<bool>())
                return true;
            if (item["isBlueprint"] != null && item["isBlueprint"].Value<bool>())
                return true;

            JArray equips = (JArray)item["content"]?["equips"];
            if (equips != null)
            {
                foreach (JToken t in equips)
                {
                    if (t is JObject equip)
                    {
                        if (equip["is_blueprint"] != null && equip["is_blueprint"].Value<bool>())
                            return true;
                        if (equip["isBlueprint"] != null && equip["isBlueprint"].Value<bool>())
                            return true;
                        if (equip["info"] is JObject info)
                        {
                            if (info["is_blueprint"] != null && info["is_blueprint"].Value<bool>())
                                return true;
                            if (info["isBlueprint"] != null && info["isBlueprint"].Value<bool>())
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private void DrawSingleItemSlot(BasePlayer player, string targetSlotId, bool remove)
        {
            if (!_playerItems.TryGetValue(player.UserIDString, out JArray items))
                return;

            int index = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if ((string)items[i]["_id"] == targetSlotId)
                {
                    index = i;
                    break;
                }
            }

            if (index == -1)
                return;

            string contentName = UIName + "_Content";
            string itemPanel = $"{UIName}_Item_{index}";

            CuiHelper.DestroyUi(player, itemPanel);

            if (remove)
            {
                // CHANGE: Проверка, остались ли еще предметы в корзине; если нет - отрисовать надпись "Ваша корзина пуста."
                int validRemaining = 0;
                foreach (JToken t in items)
                {
                    if (t is JObject obj && (int)(obj["count"] ?? 0) > 0)
                        validRemaining++;
                }

                if (validRemaining == 0)
                {
                    CuiHelper.DestroyUi(player, UIName + "_TakeAll");
                    CuiHelper.DestroyUi(player, contentName);
                    CuiHelper.DestroyUi(player, UIName + "_Empty");

                    var emptyContainer = new CuiElementContainer();
                    string emptyPanel = UIName + "_Empty";
                    emptyContainer.Add(
                        new CuiPanel
                        {
                            Image = { Color = "0 0 0 0" },
                            RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                        },
                        LayerContent,
                        emptyPanel
                    );

                    emptyContainer.Add(
                        new CuiLabel
                        {
                            Text =
                            {
                                Text = Lang("Empty", player.UserIDString),
                                FontSize = 18,
                                Align = TextAnchor.MiddleCenter,
                                Color = "0.8 0.8 0.8 1",
                                Font = "robotocondensed-bold.ttf",
                            },
                            RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                        },
                        emptyPanel
                    );

                    CuiHelper.AddUi(player, emptyContainer);
                }
                return;
            }

            var gridCfg = _config.UI.CartGrid;
            int columns = gridCfg.Columns > 0 ? gridCfg.Columns : 5;

            float totalGridWidth = (columns * gridCfg.CardWidth) + ((columns - 1) * gridCfg.GapX);
            float startX = -(totalGridWidth / 2f) + (gridCfg.CardWidth / 2f);
            float startY = -(gridCfg.CardHeight / 2f) - gridCfg.Padding;

            JObject item = (JObject)items[index];
            string slotId = (string)item["_id"];
            string itemId = (string)item["meta"]?["_item"] ?? slotId;
            string title = (string)item["title"];
            int count = (int)item["count"];
            string logoLink = (string)item["logoLink"];

            bool isBlueprint = CheckIsBlueprint(item);

            bool hasState = _slotStates.TryGetValue(slotId, out string slotState);
            bool isNoSpace = hasState && slotState.StartsWith("nospace");
            bool isBlocked = IsItemBlocked(itemId, out float remaining);

            string cardBg = gridCfg.CardBgColor;
            if (hasState && slotState == "success")
                cardBg = gridCfg.SuccessBgColor;
            else if (isBlocked)
                cardBg = gridCfg.BlockedBgColor;

            int row = index / columns;
            int col = index % columns;
            float currentX = startX + (col * (gridCfg.CardWidth + gridCfg.GapX));
            float currentY = startY - (row * (gridCfg.CardHeight + gridCfg.GapY));

            float halfW = gridCfg.CardWidth / 2f;
            float halfH = gridCfg.CardHeight / 2f;
            float minX = currentX - halfW;
            float maxX = currentX + halfW;
            float minY = currentY - halfH;
            float maxY = currentY + halfH;

            CuiElementContainer container = new CuiElementContainer();

            // Card Background (Pure Vector Panel)
            container.Add(
                new CuiPanel
                {
                    Image = { Color = cardBg },
                    RectTransform =
                    {
                        AnchorMin = "0.5 1",
                        AnchorMax = "0.5 1",
                        OffsetMin =
                            $"{minX.ToString(System.Globalization.CultureInfo.InvariantCulture)} {minY.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                        OffsetMax =
                            $"{maxX.ToString(System.Globalization.CultureInfo.InvariantCulture)} {maxY.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    },
                },
                contentName,
                itemPanel
            );

            // Item Title at the top
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = title,
                        FontSize = gridCfg.TitleFontSize,
                        Align = TextAnchor.MiddleCenter,
                        Color = gridCfg.TitleColor,
                        Font = "robotocondensed-bold.ttf",
                    },
                    RectTransform = { AnchorMin = "0.05 0.72", AnchorMax = "0.95 0.96" },
                },
                itemPanel
            );

            float imgHalf = gridCfg.ImageSize / 2f;
            float imgY = gridCfg.ImageOffsetY;

            // CHANGE: Отрисовка изображения чертежа по URL на заднем плане предмета
            if (isBlueprint && !string.IsNullOrEmpty(_blueprintIconPngId))
            {
                container.Add(
                    new CuiElement
                    {
                        Parent = itemPanel,
                        Name = itemPanel + "_Blueprint",
                        Components =
                        {
                            new CuiRawImageComponent { Png = _blueprintIconPngId },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{(-imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)} {(imgY - imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                OffsetMax =
                                    $"{imgHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(imgY + imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                            },
                        },
                    }
                );
            }

            // Item Icon (if downloaded)
            if (
                !string.IsNullOrEmpty(logoLink)
                && _imageCache.TryGetValue(logoLink, out string logoPng)
            )
            {
                container.Add(
                    new CuiElement
                    {
                        Parent = itemPanel,
                        Name = itemPanel + "_Icon",
                        Components =
                        {
                            new CuiRawImageComponent { Png = logoPng },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{(-imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)} {(imgY - imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                OffsetMax =
                                    $"{imgHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(imgY + imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                            },
                        },
                    }
                );
            }

            // Count at bottom right
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = $"x{count}",
                        FontSize = gridCfg.CountFontSize,
                        Align = TextAnchor.LowerRight,
                        Color = gridCfg.CountColor,
                        Font = "robotocondensed-bold.ttf",
                    },
                    RectTransform = { AnchorMin = "0 0.05", AnchorMax = "0.92 0.28" },
                },
                itemPanel
            );

            // Overlays (Success / NoSpace / Blocked)
            if (hasState && slotState == "success")
            {
                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = gridCfg.SuccessBgColor },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    itemPanel,
                    $"{itemPanel}_Dim"
                );
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Lang("Taken", player.UserIDString),
                            FontSize = 16,
                            Align = TextAnchor.MiddleCenter,
                            Color = "0.4 0.9 0.4 1",
                            Font = "robotocondensed-bold.ttf",
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    $"{itemPanel}_Dim"
                );
            }
            else if (isNoSpace)
            {
                string countNeeded = slotState.Split(':')[1];
                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = gridCfg.BlockedBgColor },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    itemPanel,
                    $"{itemPanel}_Dim"
                );
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = string.Format(
                                Lang("NeedSlots", player.UserIDString),
                                countNeeded
                            ),
                            FontSize = 13,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 0.7 0.7 1",
                            Font = "robotocondensed-bold.ttf",
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    $"{itemPanel}_Dim"
                );
            }
            else if (isBlocked)
            {
                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = gridCfg.BlockedBgColor },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    itemPanel,
                    $"{itemPanel}_Dim"
                );
                container.Add(
                    new CuiElement
                    {
                        Parent = $"{itemPanel}_Dim",
                        Components =
                        {
                            new CuiTextComponent
                            {
                                Text = "%TIME_LEFT%",
                                FontSize = 14,
                                Align = TextAnchor.MiddleCenter,
                                Color = "1 0.8 0.2 1",
                                Font = "robotocondensed-bold.ttf",
                            },
                            new CuiCountdownComponent
                            {
                                StartTime = (int)remaining,
                                EndTime = 0,
                                Step = 1,
                                TimerFormat = Oxide
                                    .Game
                                    .Rust
                                    .Cui
                                    .TimerFormat
                                    .DaysHoursMinutesSeconds,
                            },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                        },
                    }
                );
            }
            else
            {
                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Color = "0 0 0 0",
                            Command = $"rsurvivalstore.take {slotId} {itemId}",
                        },
                        Text = { Text = "" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    itemPanel
                );
            }

            CuiHelper.AddUi(player, container);
        }

        private void DrawUI(BasePlayer player, JArray items, bool isAuthError = false)
        {
            if (player == null || !player.IsConnected)
                return;

            if (items != null)
                _playerItems[player.UserIDString] = items;

            bool isEmbedded = _activeAddonParent.TryGetValue(player.userID, out string parentName);
            string targetParent = isEmbedded
                ? (!string.IsNullOrEmpty(parentName) ? parentName : "Overlay")
                : "Overlay";

            CuiHelper.DestroyUi(player, UIName);
            CuiHelper.DestroyUi(player, LayerBg);
            CuiHelper.DestroyUi(player, LayerStatic);
            CuiHelper.DestroyUi(player, LayerHeader);
            CuiHelper.DestroyUi(player, LayerContent);
            CuiHelper.DestroyUi(player, UIName + "_TakeAll");
            CuiHelper.DestroyUi(player, UIName + "_Content");
            CuiHelper.DestroyUi(player, UIName + "_Empty");

            var mainContainer = new CuiElementContainer();

            if (!isEmbedded)
            {
                // Background overlay for standalone /store
                mainContainer.Add(
                    new CuiPanel
                    {
                        Image =
                        {
                            Color = _config.UI.MainBgColor,
                            Material = "assets/content/ui/uibackgroundblur.mat",
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                        CursorEnabled = true,
                    },
                    "Overlay",
                    LayerBg
                );

                mainContainer.Add(
                    new CuiButton
                    {
                        Button = { Color = "0 0 0 0", Command = "rsurvivalstore.close" },
                        Text = { Text = "" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    LayerBg
                );

                mainContainer.Add(
                    new CuiElement
                    {
                        Parent = LayerBg,
                        Name = LayerStatic,
                        Components =
                        {
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                        },
                    }
                );
            }
            else
            {
                mainContainer.Add(
                    new CuiElement
                    {
                        Parent = targetParent,
                        Name = LayerBg,
                        Components =
                        {
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                        },
                    }
                );

                mainContainer.Add(
                    new CuiElement
                    {
                        Parent = LayerBg,
                        Name = LayerStatic,
                        Components =
                        {
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                        },
                    }
                );
            }

            float baseX = isEmbedded ? _config.UI.EmbedOffsetX : _config.UI.StandaloneOffsetX;

            // 1. Верхняя панель (Заголовок)
            var headerCfg = _config.UI.HeaderPanel;
            float hW = headerCfg.Width;
            float hH = headerCfg.Height;
            float hY = headerCfg.OffsetY;
            float hXmin = baseX - (hW / 2f);
            float hXmax = baseX + (hW / 2f);
            float hYmin = hY - (hH / 2f);
            float hYmax = hY + (hH / 2f);

            mainContainer.Add(
                new CuiPanel
                {
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{hXmin.ToString(System.Globalization.CultureInfo.InvariantCulture)} {hYmin.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                        OffsetMax =
                            $"{hXmax.ToString(System.Globalization.CultureInfo.InvariantCulture)} {hYmax.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    },
                    Image = { Color = headerCfg.BgColor },
                },
                LayerStatic,
                LayerHeader
            );

            mainContainer.Add(
                new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin =
                            $"{headerCfg.TitlePaddingLeft.ToString(System.Globalization.CultureInfo.InvariantCulture)} 0",
                        OffsetMax =
                            $"{(-headerCfg.TitlePaddingRight).ToString(System.Globalization.CultureInfo.InvariantCulture)} 0",
                    },
                    Text =
                    {
                        Text = Lang("Title", player.UserIDString),
                        Align = TextAnchor.MiddleLeft,
                        FontSize = headerCfg.TitleSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = headerCfg.TitleColor,
                    },
                },
                LayerHeader
            );

            // Кнопка "Забрать всё" в шапке (если предметов > 1)
            int validItemsCount = 0;
            if (items != null)
            {
                foreach (JToken t in items)
                {
                    if (t is JObject obj && (int)(obj["count"] ?? 0) > 0)
                        validItemsCount++;
                }
            }

            var takeAllCfg = _config.UI.TakeAllButton;
            if (takeAllCfg != null && validItemsCount > 1)
            {
                float tW = takeAllCfg.Width;
                float tH = takeAllCfg.Height;
                float tX = takeAllCfg.OffsetX;
                float tY = takeAllCfg.OffsetY;

                mainContainer.Add(
                    new CuiPanel
                    {
                        Image = { Color = takeAllCfg.BgColor },
                        RectTransform =
                        {
                            AnchorMin = "1 0.5",
                            AnchorMax = "1 0.5",
                            OffsetMin =
                                $"{(tX - tW).ToString(System.Globalization.CultureInfo.InvariantCulture)} {(tY - (tH / 2f)).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                            OffsetMax =
                                $"{tX.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(tY + (tH / 2f)).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                        },
                    },
                    LayerHeader,
                    UIName + "_TakeAll"
                );

                mainContainer.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Lang("TakeAll", player.UserIDString),
                            FontSize = takeAllCfg.FontSize,
                            Align = TextAnchor.MiddleCenter,
                            Font = "robotocondensed-bold.ttf",
                            Color = takeAllCfg.TextColor,
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    UIName + "_TakeAll"
                );

                mainContainer.Add(
                    new CuiButton
                    {
                        Button = { Command = "rsurvivalstore.takeall", Color = "0 0 0 0" },
                        Text = { Text = "" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    UIName + "_TakeAll"
                );
            }

            // 2. Нижняя панель (Контент)
            var contentCfg = _config.UI.ContentPanel;
            float cW = contentCfg.Width;
            float cH = contentCfg.Height;
            float cY = contentCfg.OffsetY;
            float cXmin = baseX - (cW / 2f);
            float cXmax = baseX + (cW / 2f);
            float cYmin = cY - (cH / 2f);
            float cYmax = cY + (cH / 2f);

            mainContainer.Add(
                new CuiPanel
                {
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{cXmin.ToString(System.Globalization.CultureInfo.InvariantCulture)} {cYmin.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                        OffsetMax =
                            $"{cXmax.ToString(System.Globalization.CultureInfo.InvariantCulture)} {cYmax.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    },
                    Image = { Color = contentCfg.BgColor },
                },
                LayerStatic,
                LayerContent
            );

            // Содержимое контента
            if (isAuthError)
            {
                string emptyPanel = UIName + "_Empty";
                mainContainer.Add(
                    new CuiPanel
                    {
                        Image = { Color = "0 0 0 0" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    LayerContent,
                    emptyPanel
                );

                mainContainer.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Lang("NotAuthorized", player.UserIDString),
                            FontSize = 18,
                            Align = TextAnchor.LowerCenter,
                            Color = "0.9 0.4 0.4 1",
                        },
                        RectTransform = { AnchorMin = "0 0.55", AnchorMax = "1 1" },
                    },
                    emptyPanel
                );

                mainContainer.Add(
                    new CuiElement
                    {
                        Parent = emptyPanel,
                        Components =
                        {
                            new CuiInputFieldComponent
                            {
                                Text = _config.Settings.ShopURL,
                                FontSize = 18,
                                Align = TextAnchor.UpperCenter,
                                Color = "1 0.66 0 1",
                                ReadOnly = true,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 0.5",
                            },
                        },
                    }
                );
            }
            else if (items == null || items.Count == 0 || validItemsCount == 0)
            {
                string emptyPanel = UIName + "_Empty";
                mainContainer.Add(
                    new CuiPanel
                    {
                        Image = { Color = "0 0 0 0" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    LayerContent,
                    emptyPanel
                );

                mainContainer.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Lang("Empty", player.UserIDString),
                            FontSize = 18,
                            Align = TextAnchor.MiddleCenter,
                            Color = "0.8 0.8 0.8 1",
                            Font = "robotocondensed-bold.ttf",
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    emptyPanel
                );
            }
            else
            {
                var gridCfg = _config.UI.CartGrid;
                int columns = gridCfg.Columns > 0 ? gridCfg.Columns : 5;

                float totalGridWidth =
                    (columns * gridCfg.CardWidth) + ((columns - 1) * gridCfg.GapX);
                float startX = -(totalGridWidth / 2f) + (gridCfg.CardWidth / 2f);
                float startY = -(gridCfg.CardHeight / 2f) - gridCfg.Padding;

                int totalRows = Mathf.CeilToInt((float)items.Count / columns);
                float totalContentHeight =
                    totalRows * (gridCfg.CardHeight + gridCfg.GapY) + (gridCfg.Padding * 2f);
                float viewportHeight = contentCfg.Height * 0.92f;

                if (totalContentHeight < viewportHeight)
                    totalContentHeight = viewportHeight;

                string contentName = UIName + "_Content";

                // Native ScrollView
                mainContainer.Add(
                    new CuiElement
                    {
                        Parent = LayerContent,
                        Name = contentName,
                        Components =
                        {
                            new CuiImageComponent { Color = "0 0 0 0" },
                            new CuiScrollViewComponent
                            {
                                ContentTransform = new CuiRectTransformComponent
                                {
                                    AnchorMin = "0 1",
                                    AnchorMax = "1 1",
                                    OffsetMin =
                                        $"0 -{totalContentHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                    OffsetMax = "0 0",
                                },
                                Vertical = true,
                                Horizontal = false,
                                Inertia = true,
                                ScrollSensitivity = 30f,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.02 0.03",
                                AnchorMax = "0.98 0.97",
                            },
                        },
                    }
                );

                for (int i = 0; i < items.Count; i++)
                {
                    JObject item = (JObject)items[i];
                    string slotId = (string)item["_id"];
                    string itemId = (string)item["meta"]?["_item"] ?? slotId;
                    string title = (string)item["title"];
                    int count = (int)item["count"];
                    string logoLink = (string)item["logoLink"];

                    bool isBlueprint = CheckIsBlueprint(item);

                    bool hasState = _slotStates.TryGetValue(slotId, out string slotState);
                    bool isNoSpace = hasState && slotState.StartsWith("nospace");
                    bool isBlocked = IsItemBlocked(itemId, out float remaining);

                    string cardBg = gridCfg.CardBgColor;
                    if (hasState && slotState == "success")
                        cardBg = gridCfg.SuccessBgColor;
                    else if (isBlocked)
                        cardBg = gridCfg.BlockedBgColor;

                    string itemPanel = $"{UIName}_Item_{i}";

                    int row = i / columns;
                    int col = i % columns;
                    float currentX = startX + (col * (gridCfg.CardWidth + gridCfg.GapX));
                    float currentY = startY - (row * (gridCfg.CardHeight + gridCfg.GapY));

                    float halfW = gridCfg.CardWidth / 2f;
                    float halfH = gridCfg.CardHeight / 2f;
                    float minX = currentX - halfW;
                    float maxX = currentX + halfW;
                    float minY = currentY - halfH;
                    float maxY = currentY + halfH;

                    mainContainer.Add(
                        new CuiPanel
                        {
                            Image = { Color = cardBg },
                            RectTransform =
                            {
                                AnchorMin = "0.5 1",
                                AnchorMax = "0.5 1",
                                OffsetMin =
                                    $"{minX.ToString(System.Globalization.CultureInfo.InvariantCulture)} {minY.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                OffsetMax =
                                    $"{maxX.ToString(System.Globalization.CultureInfo.InvariantCulture)} {maxY.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                            },
                        },
                        contentName,
                        itemPanel
                    );

                    // Add Title at the top
                    mainContainer.Add(
                        new CuiLabel
                        {
                            Text =
                            {
                                Text = title,
                                FontSize = gridCfg.TitleFontSize,
                                Align = TextAnchor.MiddleCenter,
                                Color = gridCfg.TitleColor,
                                Font = "robotocondensed-bold.ttf",
                            },
                            RectTransform = { AnchorMin = "0.05 0.72", AnchorMax = "0.95 0.96" },
                        },
                        itemPanel
                    );

                    float imgHalf = gridCfg.ImageSize / 2f;
                    float imgY = gridCfg.ImageOffsetY;

                    // CHANGE: Отрисовка изображения чертежа по URL на заднем плане предмета
                    if (isBlueprint && !string.IsNullOrEmpty(_blueprintIconPngId))
                    {
                        mainContainer.Add(
                            new CuiElement
                            {
                                Parent = itemPanel,
                                Name = itemPanel + "_Blueprint",
                                Components =
                                {
                                    new CuiRawImageComponent { Png = _blueprintIconPngId },
                                    new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0.5 0.5",
                                        AnchorMax = "0.5 0.5",
                                        OffsetMin =
                                            $"{(-imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)} {(imgY - imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                        OffsetMax =
                                            $"{imgHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(imgY + imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                    },
                                },
                            }
                        );
                    }

                    // Item Icon
                    if (
                        !string.IsNullOrEmpty(logoLink)
                        && _imageCache.TryGetValue(logoLink, out string logoPng)
                    )
                    {
                        mainContainer.Add(
                            new CuiElement
                            {
                                Parent = itemPanel,
                                Name = itemPanel + "_Icon",
                                Components =
                                {
                                    new CuiRawImageComponent { Png = logoPng },
                                    new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0.5 0.5",
                                        AnchorMax = "0.5 0.5",
                                        OffsetMin =
                                            $"{(-imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)} {(imgY - imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                        OffsetMax =
                                            $"{imgHalf.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(imgY + imgHalf).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                                    },
                                },
                            }
                        );
                    }

                    // Add Count at the bottom right
                    mainContainer.Add(
                        new CuiLabel
                        {
                            Text =
                            {
                                Text = $"x{count}",
                                FontSize = gridCfg.CountFontSize,
                                Align = TextAnchor.LowerRight,
                                Color = gridCfg.CountColor,
                                Font = "robotocondensed-bold.ttf",
                            },
                            RectTransform = { AnchorMin = "0 0.05", AnchorMax = "0.92 0.28" },
                        },
                        itemPanel
                    );

                    if (hasState && slotState == "success")
                    {
                        mainContainer.Add(
                            new CuiPanel
                            {
                                Image = { Color = gridCfg.SuccessBgColor },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            itemPanel,
                            $"{itemPanel}_Dim"
                        );
                        mainContainer.Add(
                            new CuiLabel
                            {
                                Text =
                                {
                                    Text = Lang("Taken", player.UserIDString),
                                    FontSize = 16,
                                    Align = TextAnchor.MiddleCenter,
                                    Color = "0.4 0.9 0.4 1",
                                    Font = "robotocondensed-bold.ttf",
                                },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            $"{itemPanel}_Dim"
                        );
                    }
                    else if (isNoSpace)
                    {
                        string countNeeded = slotState.Split(':')[1];
                        mainContainer.Add(
                            new CuiPanel
                            {
                                Image = { Color = gridCfg.BlockedBgColor },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            itemPanel,
                            $"{itemPanel}_Dim"
                        );
                        mainContainer.Add(
                            new CuiLabel
                            {
                                Text =
                                {
                                    Text = string.Format(
                                        Lang("NeedSlots", player.UserIDString),
                                        countNeeded
                                    ),
                                    FontSize = 13,
                                    Align = TextAnchor.MiddleCenter,
                                    Color = "1 0.7 0.7 1",
                                    Font = "robotocondensed-bold.ttf",
                                },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            $"{itemPanel}_Dim"
                        );
                    }
                    else if (isBlocked)
                    {
                        mainContainer.Add(
                            new CuiPanel
                            {
                                Image = { Color = gridCfg.BlockedBgColor },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            itemPanel,
                            $"{itemPanel}_Dim"
                        );
                        mainContainer.Add(
                            new CuiElement
                            {
                                Parent = $"{itemPanel}_Dim",
                                Components =
                                {
                                    new CuiTextComponent
                                    {
                                        Text = "%TIME_LEFT%",
                                        FontSize = 14,
                                        Align = TextAnchor.MiddleCenter,
                                        Color = "1 0.8 0.2 1",
                                        Font = "robotocondensed-bold.ttf",
                                    },
                                    new CuiCountdownComponent
                                    {
                                        StartTime = (int)remaining,
                                        EndTime = 0,
                                        Step = 1,
                                        TimerFormat = Oxide
                                            .Game
                                            .Rust
                                            .Cui
                                            .TimerFormat
                                            .DaysHoursMinutesSeconds,
                                    },
                                    new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0 0",
                                        AnchorMax = "1 1",
                                    },
                                },
                            }
                        );
                    }
                    else
                    {
                        // Invisible Full-cell Button to Take
                        mainContainer.Add(
                            new CuiButton
                            {
                                Button =
                                {
                                    Color = "0 0 0 0",
                                    Command = $"rsurvivalstore.take {slotId} {itemId}",
                                },
                                Text = { Text = "" },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            itemPanel
                        );
                    }
                }
            }

            CuiHelper.AddUi(player, mainContainer);
        }

        #endregion

        #region Take Item Logic

        [ConsoleCommand("rsurvivalstore.takeall")]
        private void CmdTakeAll(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (!_playerItems.TryGetValue(player.UserIDString, out JArray items))
            {
                return;
            }

            int freeSlots =
                player.inventory.containerMain.capacity
                - player.inventory.containerMain.itemList.Count;
            freeSlots +=
                player.inventory.containerBelt.capacity
                - player.inventory.containerBelt.itemList.Count;

            float takeDelay = 0f;

            foreach (JToken token in items)
            {
                JObject item = token as JObject;
                if (item == null)
                    continue;

                string slotId = (string)item["_id"];
                string itemId =
                    (string)item["meta"]?["_item"] != null ? (string)item["meta"]["_item"] : slotId;
                if (string.IsNullOrEmpty(slotId))
                    continue;

                if (IsItemBlocked(itemId, out float remaining))
                    continue;

                int maxCount = (int)(item["count"] ?? 0);
                if (maxCount <= 0)
                    continue;

                string title = (string)item["title"] ?? "";
                bool isBackpack =
                    title.IndexOf("[backpack]", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("[рюкзак]", StringComparison.OrdinalIgnoreCase) >= 0;

                int quantityToTake = 1;
                if (
                    title.IndexOf("[superstack]", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("[суперстак]", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("[++]", StringComparison.OrdinalIgnoreCase) >= 0
                )
                {
                    quantityToTake = maxCount;
                }

                if (!isBackpack)
                {
                    int requiredSlots = CalculateRequiredSlots(item, quantityToTake);
                    if (freeSlots < requiredSlots)
                    {
                        // Simulate CmdTake visual feedback for items that do not fit
                        _slotStates[slotId] = $"nospace:{requiredSlots - freeSlots}";
                        DrawSingleItemSlot(player, slotId, false);

                        timer.Once(
                            1.5f,
                            () =>
                            {
                                _slotStates.Remove(slotId);
                                DrawSingleItemSlot(player, slotId, false); // Re-draw back to normal state
                            }
                        );
                        continue;
                    }
                    freeSlots -= requiredSlots;
                }

                float currentDelay = takeDelay;
                string captureSlot = slotId;
                string captureItem = itemId;

                timer.Once(
                    currentDelay,
                    () =>
                    {
                        if (player != null && player.IsConnected)
                        {
                            player.SendConsoleCommand(
                                $"rsurvivalstore.take \"{captureSlot}\" \"{captureItem}\""
                            );
                        }
                    }
                );

                takeDelay += 0.5f; // Add a 0.5s delay between each item to prevent backend database race condition (duping)
            }
        }

        [ConsoleCommand("rsurvivalstore.take")]
        private void CmdTake(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !arg.HasArgs(2))
                return;

            string slotId = arg.GetString(0);
            string itemId = arg.GetString(1);

            if (IsItemBlocked(itemId, out float remaining))
            {
                return;
            }

            var data = new Dictionary<string, object>
            {
                ["siteId"] = _config.Settings.SiteID,
                ["clientSid"] = player.UserIDString,
                ["slotId"] = slotId,
            };

            SendApiRequest(
                "client.getInventorySlot",
                data,
                (code, response) =>
                {
                    if (code != 200 || string.IsNullOrEmpty(response))
                    {
                        Player.Message(player, Lang("ApiError", player.UserIDString));
                        return;
                    }

                    try
                    {
                        JObject json = JObject.Parse(response);
                        JObject slot = (JObject)json["response"];

                        if (slot == null || (int)slot["count"] <= 0)
                            return;

                        int maxCount = (int)slot["count"];
                        int quantityToTake = 1;

                        string title = (string)slot["title"] ?? "";
                        if (
                            title.IndexOf("[superstack]", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[суперстак]", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[++]", StringComparison.OrdinalIgnoreCase) >= 0
                        )
                        {
                            quantityToTake = maxCount;
                        }

                        bool isBackpack =
                            title.IndexOf("[backpack]", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[рюкзак]", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (!isBackpack)
                        {
                            int requiredSlots = CalculateRequiredSlots(slot, quantityToTake);
                            int freeSlots =
                                player.inventory.containerMain.capacity
                                - player.inventory.containerMain.itemList.Count;
                            freeSlots +=
                                player.inventory.containerBelt.capacity
                                - player.inventory.containerBelt.itemList.Count;

                            if (freeSlots < requiredSlots)
                            {
                                // Visual feedback directly in the slot instead of chat
                                _slotStates[slotId] = $"nospace:{requiredSlots - freeSlots}";
                                DrawSingleItemSlot(player, slotId, false);

                                timer.Once(
                                    1.5f,
                                    () =>
                                    {
                                        _slotStates.Remove(slotId);
                                        DrawSingleItemSlot(player, slotId, false); // Re-draw back to normal state
                                    }
                                );

                                return;
                            }
                        }

                        var actData = new Dictionary<string, object>
                        {
                            ["siteId"] = _config.Settings.SiteID,
                            ["clientSid"] = player.UserIDString,
                            ["slotId"] = slotId,
                            ["quantity"] = quantityToTake,
                        };

                        SendApiRequest(
                            "items.activate",
                            actData,
                            (aCode, aResponse) =>
                            {
                                if (aCode == 200)
                                {
                                    GiveItemToPlayer(player, slot, quantityToTake);

                                    // Update local cache
                                    if (
                                        _playerItems.TryGetValue(
                                            player.UserIDString,
                                            out JArray pItems
                                        )
                                    )
                                    {
                                        int validItems = 0;
                                        foreach (JToken t in pItems)
                                        {
                                            JObject pItem = t as JObject;
                                            if (pItem != null)
                                            {
                                                if ((string)pItem["_id"] == slotId)
                                                {
                                                    if (maxCount > quantityToTake)
                                                    {
                                                        pItem["count"] = maxCount - quantityToTake;
                                                        validItems++;
                                                    }
                                                    else
                                                    {
                                                        pItem["count"] = 0; // Mark as fully taken
                                                    }
                                                }
                                                else if ((int)(pItem["count"] ?? 0) > 0)
                                                {
                                                    validItems++;
                                                }
                                            }
                                        }

                                        if (validItems <= 1)
                                        {
                                            CuiHelper.DestroyUi(player, UIName + "_TakeAll");
                                        }
                                    }

                                    if (maxCount > quantityToTake)
                                    {
                                        _slotStates[slotId] = "success";
                                        DrawSingleItemSlot(player, slotId, false);

                                        timer.Once(
                                            1.5f,
                                            () =>
                                            {
                                                _slotStates.Remove(slotId);
                                                DrawSingleItemSlot(player, slotId, false);
                                            }
                                        );
                                    }
                                    else
                                    {
                                        _slotStates[slotId] = "success";
                                        DrawSingleItemSlot(player, slotId, false);

                                        timer.Once(
                                            0.5f,
                                            () =>
                                            {
                                                _slotStates.Remove(slotId);
                                                DrawSingleItemSlot(player, slotId, true);
                                            }
                                        );
                                    }
                                }
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        PrintError($"Error processing item take: {ex.Message}");
                    }
                }
            );
        }

        private int CalculateRequiredSlots(JObject slot, int quantityMultiplier)
        {
            int requiredSlots = 0;
            JArray equips = (JArray)slot["content"]?["equips"];
            if (equips != null)
            {
                foreach (JToken token in equips)
                {
                    JObject equip = token as JObject;
                    if (equip == null)
                        continue;

                    JObject info = equip["info"] as JObject;
                    if (info == null)
                        continue;

                    string type = (string)info["type"];

                    if (type == "item")
                    {
                        // Each item configuration will result in 1 slot being used,
                        // as GiveItemToPlayer creates exactly 1 Item instance per equip regardless of quantityMultiplier.
                        requiredSlots++;
                    }
                }
            }

            if (requiredSlots == 0)
                requiredSlots = 1;

            return requiredSlots;
        }

        private void GiveItemToPlayer(BasePlayer player, JObject slot, int quantityMultiplier = 1)
        {
            string title = (string)slot["title"] ?? "";
            bool isBackpack =
                title.IndexOf("[backpack]", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("[рюкзак]", StringComparison.OrdinalIgnoreCase) >= 0;

            List<Item> backpackItems = isBackpack ? new List<Item>() : null;

            JArray equips = (JArray)slot["content"]?["equips"];
            if (equips != null)
            {
                foreach (JObject equip in equips)
                {
                    JObject info = (JObject)equip["info"];
                    string type = (string)info["type"];

                    if (type == "item")
                    {
                        string shortname = (string)info["bpPath"];
                        int count = (int)equip["count"] * quantityMultiplier;

                        bool isBlueprint = CheckIsBlueprint(slot);

                        if (isBlueprint)
                        {
                            Item item = ItemManager.CreateByName("blueprintbase", count);
                            if (item != null)
                            {
                                var def = ItemManager.FindItemDefinition(shortname);
                                if (def != null)
                                    item.blueprintTarget = def.itemid;

                                if (isBackpack)
                                {
                                    backpackItems.Add(item);
                                }
                                else if (!player.inventory.GiveItem(item))
                                {
                                    item.Drop(player.GetDropPosition(), player.GetDropVelocity());
                                }
                            }
                        }
                        else
                        {
                            Item item = ItemManager.CreateByName(shortname, count);
                            if (item != null)
                            {
                                if (isBackpack)
                                {
                                    backpackItems.Add(item);
                                }
                                else if (!player.inventory.GiveItem(item))
                                {
                                    item.Drop(player.GetDropPosition(), player.GetDropVelocity());
                                }
                            }
                        }
                    }
                    else if (type == "prefab")
                    {
                        string className = (string)info["className"];
                        if (className == "vehicle")
                        {
                            for (int i = 0; i < quantityMultiplier; i++)
                            {
                                string shortname = (string)info["bpPath"];
                                BaseEntity ent = GameManager.server.CreateEntity(
                                    shortname,
                                    player.transform.position + player.transform.forward * 3f
                                );
                                if (ent != null)
                                {
                                    ent.OwnerID = player.userID;
                                    ent.Spawn();
                                }
                            }
                        }
                    }
                }
            }

            JArray cmds = (JArray)slot["content"]?["cmds"];
            if (cmds != null)
            {
                foreach (JObject cmd in cmds)
                {
                    string raw = (string)cmd["raw"];
                    if (!string.IsNullOrEmpty(raw))
                    {
                        raw = raw.Replace("{player.sid}", player.UserIDString)
                            .Replace("{player.name}", player.displayName)
                            .Replace("{item.name}", (string)slot["title"])
                            .Replace("{item.count}", "1");
                        ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), raw);
                    }
                }
            }

            if (isBackpack && backpackItems != null && backpackItems.Count > 0)
            {
                DropItemsInBackpack(player, backpackItems);
            }
        }

        private void DropItemsInBackpack(BasePlayer player, List<Item> items)
        {
            if (items == null || items.Count == 0)
                return;

            string prefab = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";
            Vector3 pos =
                player.transform.position + player.transform.forward * 1.5f + Vector3.up * 0.5f;

            BaseEntity ent = GameManager.server.CreateEntity(prefab, pos);
            if (ent != null)
            {
                DroppedItemContainer container = ent as DroppedItemContainer;
                if (container != null)
                {
                    container.inventory = new ItemContainer();
                    container.inventory.ServerInitialize(null, items.Count);
                    container.inventory.GiveUID();

                    foreach (Item item in items)
                    {
                        if (!item.MoveToContainer(container.inventory))
                            item.Drop(pos, Vector3.zero);
                    }

                    container.Spawn();
                }
                else
                {
                    ent.Spawn();
                    foreach (var item in items)
                        item.Drop(pos, Vector3.zero);
                }
            }
            else
            {
                foreach (var item in items)
                    item.Drop(player.GetDropPosition(), player.GetDropVelocity());
            }
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["Title"] = "CART",
                    ["TakeAll"] = "CLAIM ALL",
                    ["Empty"] = "Your inventory is empty.",
                    ["Taken"] = "TAKEN",
                    ["ApiError"] = "Failed to communicate with the shop API.",
                    ["ItemBlocked"] = "Blocked! Time left: {0}",
                    ["NeedSlots"] = "NEED\n{0} SLOTS",
                    ["BlockedInTC"] = "You cannot open the store in an enemy building zone.",
                    ["BlockedInRaid"] = "You cannot open the store while raid blocked.",
                    ["BlockedInCombat"] = "You cannot open the store while in combat.",
                    ["NotConfigured"] = "The store is not yet configured by the administrator.",
                    ["NotAuthorized"] = "You are not authorized in the store!\nPlease log in at:",
                },
                this
            );

            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["Title"] = "КОРЗИНА",
                    ["TakeAll"] = "ЗАБРАТЬ ВСЁ",
                    ["Empty"] = "Ваша корзина пуста.",
                    ["Taken"] = "ВЗЯТО",
                    ["ApiError"] = "Ошибка связи с сервером магазина.",
                    ["ItemBlocked"] = "Блок! Осталось: {0}",
                    ["NeedSlots"] = "НУЖНО\n{0} СЛОТОВ",
                    ["BlockedInTC"] = "Вы не можете открыть магазин в зоне чужого шкафа.",
                    ["BlockedInRaid"] = "Вы не можете открыть корзину во время рейд блока.",
                    ["BlockedInCombat"] = "Вы не можете открыть корзину во время комбат блока.",
                    ["NotConfigured"] = "Магазин еще не настроен администратором.",
                    ["NotAuthorized"] =
                        "Вы не авторизованы в магазине!\nПожалуйста, авторизуйтесь по адресу:",
                },
                this,
                "ru"
            );
        }

        private string Lang(string key, string id = null, params object[] args)
        {
            return args.Length == 0
                ? lang.GetMessage(key, this, id)
                : string.Format(lang.GetMessage(key, this, id), args);
        }

        #endregion
    }
}
