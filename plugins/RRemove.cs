#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Pool = Facepunch.Pool;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
        [Info("RRemove", "RustInnovate", "1.6.7")]
    [Description("Режимное удаление построек и предметов с возвратом ресурсов и GUI-индикатором")]
    public sealed class RRemove : RustPlugin
    {
        #region Конфигурация

        private ConfigData configData = new();

        private sealed class ConfigData
        {
            // CHANGE: Единый раздел «Основные» разбит на два независимых:
            // CHANGE: настройки ремува и настройки апгрейда (функции ортогональны).
            [JsonProperty("Настройки ремува")]
            public RemoveSettings Remove = new();

            [JsonProperty("Настройки апгрейда")]
            public UpgradeSettings Upgrade = new();

            [JsonProperty("Настройки скинов построек")]
            public SkinSettings Skins = new();

            [JsonProperty("GUI")]
            public GuiSettings Gui = new();
        }

        /// <summary>
        /// Настройки режима удаления: длительность режима, проценты возврата ресурсов/объектов,
        /// правила доступа к чужим объектам, лимит времени на удаление, чёрный список shortname,
        /// активация клавишей и встроенная система рейд/комбат-блокировки.
        /// </summary>
        internal sealed class RemoveSettings
        {
            [JsonProperty("Время действия режима удаления")]
            public float ModeDuration = 30f;

            [JsonProperty("Процент возвращаемых ресурсов с построек (Максимум 1.0 - это 100%)")]
            public float BuildingRefundPercent = 1.0f;

            [JsonProperty("Процент возвращаемых ресурсов с Items (Максимум 1.0 - это 100%)")]
            public float ItemsRefundPercent = 1.0f;

            [JsonProperty("Процент выпадающих ресурсов (не вещей) с удаляемых ящиков (Максимум 1.0 - это 100%)")]
            public float ContainerResourceDropPercent = 1.0f;

            [JsonProperty("Включить запрет на удаление объекта если в его инвентаре есть предметы")]
            public bool BlockWithItems = true;

            [JsonProperty("Разрешить удаление чужих объектов при наличии авторизации в шкафу")]
            public bool AllowOthersWithAuth = false;

            [JsonProperty("Разрешить удаление собственных объектов без авторизации в шкафу")]
            public bool AllowOwnWithoutAuth = false;

            [JsonProperty("Разрешить удаление объектов соклановцев")]
            public bool AllowClanmates = false;

            [JsonProperty("Разрешить удаление обьектов команде игрока (Team)")]
            public bool AllowTeam = true;

            [JsonProperty("Включить возрат объектов (При удаление объектов(сундуки, печки и тд.) будет возращать объект а не ресурсы)")]
            public bool ReturnDeployables = true;

            [JsonProperty("Включить потерю прочности предмета (Item lose condition)")]
            public bool ItemConditionLoss = true;

            [JsonProperty("Сколько процентов прочности теряет предмет при удалении (дефолт - 10 (250 - 10% = 25))")]
            public float ConditionLossPercent = 10f;

            [JsonProperty("Включить запрет на удаление объекта для игрока после истечения N времени указанным в конфигурации")]
            public bool EnablePlaceTimeLimit = false;

            [JsonProperty("Время на запрет удаление объекта после истечения указаного времени (в секундах)")]
            public float PlaceTimeLimit = 3600.0f;

            [JsonProperty("Привилегия игнорирования запрета удаления объектов какие были установлены N времени назад (Если включено)")]
            public string IgnorePermission = "rremove.ignore";

            // CHANGE: 1.6.6 — дефолт выверен по ПРЕФАБАМ энтити (запрос владельца): нефтекубрик —
            // CHANGE: refinery_small_deployed (small.oil.refinery — shortname ПРЕДМЕТА); имена
            // CHANGE: предметов (chineselantern/tunalight/lantern) легитимны — IsBlockedShortname
            // CHANGE: с 1.6.6 сравнивает и их. Единый источник: инициализатор + миграция.
            internal static readonly string[] DefaultBlockedShortnames =
            {
                "industrial.furnace.large", // Large Industrial Furnace
                "furnace.large", // Large Furnace
                "chineselantern", // Chinese Lantern (энтити: chineselantern.deployed)
                "campfire", // Camp Fire
                "skull_fire_pit", // Skull Fire Pit
                "refinery_small_deployed", // Small Oil Refinery (предмет: small.oil.refinery)
                "tunalight", // Tuna Can Lamp (энтити: tunalight.deployed)
                "lantern", // Lantern (энтити: lantern.deployed)
            };

            // CHANGE: Снимок дефолта 1.6.3 — для канонической миграции списков,
            // CHANGE: которые владелец не редактировал вручную.
            internal static readonly string[] LegacyBlockedShortnames =
            {
                "industrial.furnace.large",
                "furnace.large",
                "chineselantern",
				"chineselanternwhite",
                "campfire",
                "skull_fire_pit",
                "small.oil.refinery",
                "tunalight",
                "lantern",
            };

            // CHANGE: ObjectCreationHandling.Replace — иначе Newtonsoft дописывает массив из файла
            // CHANGE: в список-по-умолчанию при каждой перезагрузке, и пустые строки множатся.
            [JsonProperty("Список запрещенных для удаления Entity shortname (Не Item)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlockedShortnames = new(DefaultBlockedShortnames);

            [JsonProperty("Клавиша активации режима удаления")]
            public string ActivationKey = "z";

            // CHANGE: Открытие радиального меню без бинда: перехват клиентской консольной команды
            // CHANGE: кнопки. Работает только для клавиш со своей консольной командой:
            // CHANGE: F = inventory.lighttoggle (включает фонарик — конфликт), X = vehicle.swapseats.
            // CHANGE: По умолчанию OFF — активация строго по Z через одноразовый клиентский бинд
            // CHANGE (bind z rremove.menu): клавиша Z консольной команды не имеет и сервером
            // CHANGE без клиентского бинда не ловится.
            [JsonProperty("Кнопка открытия радиального меню без бинда (X | F | OFF)")]
            public string MenuOpenButton = "OFF";

            [JsonProperty("Показывать подсказку кнопки открытия рад меню при взятии киянки или плана постройки в руки")]
            public bool ShowBindHint = true;

            [JsonProperty("Запретить использование радиального меню и функции ремува/апгрейда в рейд-блоке (true — через внешний плагин, false — встроенная система)")]
            public bool BlockInRaidZone = true;

            [JsonProperty("Запретить использование радиального меню и функции ремува/апгрейда в комбат-блоке (true — через внешний плагин, false — встроенная система)")]
            public bool BlockInCombatBlock = true;

            // CHANGE: Встроенная система блокировки (используется при false выше): блок по взрыву
            // CHANGE: по строению (рейдер + владелец) и по ПВП-урону (обе стороны).
            [JsonProperty("Длительность встроенной блокировки рейда (секунды)")]
            public int BuiltinRaidBlockDuration = 300;

            [JsonProperty("Длительность встроенной блокировки комбата (секунды)")]
            public int BuiltinCombatBlockDuration = 300;
        }

        /// <summary>
        /// Настройки режима автоапгрейда построек: длительность режима, включение функции,
        /// привилегии использования и апгрейда без затрат, требование авторизации в шкафу.
        /// </summary>
        internal sealed class UpgradeSettings
        {
            // CHANGE: Таймер режима апгрейда: каждое успешное действие сбрасывает отсчёт,
            // CHANGE: бездействие приводит к выключению режима.
            [JsonProperty("Время действия режима апгрейда (сбрасывается при каждом апгрейде)")]
            public float UpgradeModeDuration = 30f;

            // CHANGE: Блок настроек автоапгрейда (строительный план + удар киянкой).
            [JsonProperty("Включить автоапгрейд (строительный план и удар киянкой)")]
            public bool EnableAutoUpgrade = true;

            [JsonProperty("Привилегия использования автоапгрейда")]
            public string UpgradePermission = PermUpgrade;

            [JsonProperty("Привилегия апгрейда без затрат ресурсов (для администраторов)")]
            public string NoCostPermission = PermNoCost;

            // CHANGE: Требование шкафа: без авторизации в шкафу зоны автоапгрейд не работает.
            [JsonProperty("Требовать наличие шкафа для автоапгрейда (план+киянка)")]
            public bool UpgradeRequireCupboard = true;
        }

        // CHANGE: Скины построек (DLC): скин выбирается для КАЖДОГО типа апгрейда отдельно и
        // CHANGE: распространяется и на киянку, и на строительный план (единый UpgradeState.Skin).
        // CHANGE: Бесплатно скины никому не выдаются: без владения DLC они доступны только
        // CHANGE: по привилегии FreeSkinPermission (для администраторов).
        /// <summary>
        /// Настройки скинов построек (DLC): списки скинов по каждому типу апгрейда
        /// (выбор — пейджер Q/E у пунктов апгрейда в главном меню), привилегия бесплатного
        /// доступа и автозаполнение.
        /// Инварианты: скин применяется только если пара (тип, ID скина) есть в данных префаба
        /// блока, а игрок владеет Steam-предметом скина / имеет привилегию / скин alwaysUnlock.
        /// </summary>
        internal sealed class SkinSettings
        {
            [JsonProperty("Включить подменю скинов построек (работает вместе с автоапгрейдом)")]
            public bool EnableBuildSkins = true;

            // CHANGE: Ванильный пейджер: у пункта типа апгрейда в главном меню клиент рисует
            // CHANGE: кнопки Q/E (nextCommand/prevCommand) — листают скины типа без подменю.
            [JsonProperty("Листание скинов Q/E прямо в главном меню у типов апгрейда")]
            public bool EnableInlineSkinPaging = true;

            // CHANGE: true — при листании Q/E колесо мгновенно переоткрывается с новым скином
            // CHANGE: (клиент сам закрывает pie при исполнении команды — иначе обновление не показать);
            // CHANGE: false — колесо не переоткрывается (без мерцания и сброса курсора), скин показывается тостом.
            [JsonProperty("Переоткрывать радиальное меню при листании скинов Q/E (false — без мерцания, скин тостом)")]
            public bool ReopenMenuOnSkinPage = true;

            [JsonProperty("Привилегия бесплатного использования любых скинов построек (для администраторов, мимо проверки владения DLC)")]
            public string FreeSkinPermission = PermSkinsFree;

            // CHANGE: 1.6.0 — выбор цвета для скинов с палитрой (контейнеры): перед запуском
            // CHANGE: режима апгрейда открывается радиальное меню выбора цвета (колесо баллончика).
            [JsonProperty("Включить выбор цвета для скинов с палитрой (контейнеры) — перед запуском режима апгрейда")]
            public bool EnableColourSelection = true;

            [JsonProperty("Показывать пункт «Случайный цвет» в меню выбора цвета")]
            public bool ShowRandomColour = true;

            [JsonProperty("Иконка пункта «Случайный цвет»")]
            public string RandomColourIcon = "assets/icons/palette.png";

            // CHANGE: 1.6.6 — возвращён сплошной circle_closed.png: у контурного circle_open
            // CHANGE: тонируется только кольцо, центр остаётся белым — заливки цветом нет.
            // CHANGE: На наведении клиент всё равно перекрашивает иконку своим hover-цветом —
            // CHANGE: цвет в этот момент несут сектор и центральное превью.
            [JsonProperty("Иконка цветных кружков (окрашивается цветом палитры)")]
            public string ColourIcon = "assets/icons/circle_closed.png";

            // CHANGE: 1.6.4 — настройка «Режим отображения цвета пункта меню» (ColourOptionMode)
            // CHANGE: удалена по требованию владельца: colorMode пункта всегда CustomColor (0) —
            // CHANGE: иконка кружка окрашивается цветом палитры (AddPieOption).
            // CHANGE: Названия скинов больше не хранятся в конфиге — только ID; имена живут
            // CHANGE: в файлах локализации (lang/<язык>/RRemove.json, ключи Skin{ID скина}).
            [JsonProperty("Автоматически добавлять найденные в игре скины в списки ниже (названия берутся из локализации lang/<язык>/RRemove.json — ключи Skin{ID скина}; выключите, чтобы полностью управлять списками вручную)")]
            public bool AutoFillSkins = true;

            [JsonProperty("Скины — Дерево (ID скина — Steam item definition ID скина постройки; запись с ID 0 — «Без скина»; найденные ID: консольная команда rremove.skins)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<SkinEntry> WoodSkins = new() { new SkinEntry() };

            [JsonProperty("Скины — Камень", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<SkinEntry> StoneSkins = new() { new SkinEntry() };

            [JsonProperty("Скины — Металл", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<SkinEntry> MetalSkins = new() { new SkinEntry() };

            [JsonProperty("Скины — МВК", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<SkinEntry> HqmSkins = new() { new SkinEntry() };
        }

        // CHANGE: Поля «Название» и «Иконка» удалены из конфига: имя скина — ключ локализации
        // CHANGE: Skin{ID скина} (создаётся автоматически при старте, правится в lang-файле),
        // CHANGE: иконки скинов не использовались (автозаполнение всегда писало пустую строку).
        // CHANGE: Старый конфиг читается без потерь: Newtonsoft игнорирует неизвестные поля,
        // CHANGE: лишние ключи исчезнут из файла при следующем сохранении конфига.
        /// <summary>
        /// Запись скина постройки в конфиге: только Steam item definition ID скина.
        /// Отображаемое имя — из локализации: ключ Skin{ID скина}, для ID 0 — «Без скина»
        /// (ключ PieSkinNone). Инвариант: имя всегда непустое — SkinDisplayName даёт
        /// fallback на имя реестра и «ID {id}».
        /// </summary>
        internal sealed class SkinEntry
        {
            [JsonProperty("ID скина (0 — без скина)")]
            public ulong SkinId = 0UL;
        }

        #region Классы настроек GUI (каждая панель — отдельный класс)

        internal sealed class GuiSettings
        {
            [JsonProperty("Включить GUI-индикатор режима")]
            public bool Enabled = true;

            [JsonProperty("Корневая панель")]
            public RootPanelConfig Root = new();

            [JsonProperty("Заголовок")]
            public TitleConfig Title = new();

            [JsonProperty("Таймер")]
            public TimerConfig Timer = new();

            [JsonProperty("Подсказка")]
            public HintConfig Hint = new();

            [JsonProperty("Радиальное меню (нативное pie-меню клиента)")]
            public PieMenuSettings Pie = new();
        }

        internal sealed class PieMenuSettings
        {
            // CHANGE: Валидная иконка киянки из клиента — hammer.icon.png (hammer.png не существует).
            [JsonProperty("Иконка — простой ремув")]
            public string DefaultIcon = "assets/prefabs/weapons/hammer/hammer.icon.png";

            [JsonProperty("Иконка — админский ремув")]
            public string AdminIcon = "assets/icons/explosion_sprite.png";

            [JsonProperty("Иконка — удаление всех строений")]
            public string AllIcon = "assets/icons/clear.png";

            [JsonProperty("Иконка — закрыть меню")]
            public string CloseIcon = "assets/icons/close.png";

            [JsonProperty("Иконка — апгрейд: дерево")]
            public string WoodIcon = "assets/icons/level_wood.png";

            [JsonProperty("Иконка — апгрейд: камень")]
            public string StoneIcon = "assets/icons/level_stone.png";

            [JsonProperty("Иконка — апгрейд: металл")]
            public string MetalIcon = "assets/icons/level_metal.png";

            [JsonProperty("Иконка — апгрейд: МВК")]
            public string HqmIcon = "assets/icons/level_top.png";
        }

        internal sealed class RootPanelConfig
        {
            [JsonProperty("Высота")]
            public float Height = 64f;

            [JsonProperty("Ширина")]
            public float Width = 240f;

            [JsonProperty("Вверх/вниз")]
            public float OffsetY = 297f;

            [JsonProperty("Влево/вправо")]
            public float OffsetX = 0f;

            [JsonProperty("Цвет фона")]
            public string BgColor = "0.16 0.16 0.17 0.85";

            [JsonProperty("Цвет фона блока заголовка")]
            public string HeaderBgColor = "0.20 0.62 0.90 0.95";

            [JsonProperty("Высота блока заголовка")]
            public float HeaderHeight = 24f;
        }

        // CHANGE: Тексты заголовка и подсказки перенесены из конфига в локализацию (ключи GuiTitle/GuiHint).
        internal sealed class TitleConfig
        {
            [JsonProperty("Размер шрифта")]
            public int FontSize = 13;

            [JsonProperty("Цвет текста")]
            public string Color = "1 1 1 1";
        }

        internal sealed class TimerConfig
        {
            [JsonProperty("Размер шрифта")]
            public int FontSize = 18;

            [JsonProperty("Цвет текста")]
            public string Color = "0.25 0.69 1 1";
        }

        internal sealed class HintConfig
        {
            [JsonProperty("Высота")]
            public float Height = 18f;

            [JsonProperty("Размер шрифта")]
            public int FontSize = 11;

            [JsonProperty("Цвет текста")]
            public string Color = "0.75 0.75 0.75 1";
        }

        #endregion Классы настроек GUI

        /// <inheritdoc />
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                // CHANGE: Миграция v1→v2: раздел «Основные» разбит на два. Переносим старые
                // CHANGE: значения ДО основной десериализации, иначе они обнулятся на дефолты.
                MigrateLegacyConfig();
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                    throw new Exception();
                // CHANGE: 1.6.3 — старый пустой дефолт [""] заполняется содержательным списком.
                MigrateBlockedShortnames();
                NormalizeBlockedShortnames();
                // CHANGE: Автофикс старого конфига: несуществующая иконка hammer.png давала
                // CHANGE: флуд "FileSystem Not Found" на клиенте при каждом открытии меню.
                if (configData.Gui.Pie.DefaultIcon == "assets/icons/hammer.png")
                    configData.Gui.Pie.DefaultIcon = "assets/prefabs/weapons/hammer/hammer.icon.png";
                if (configData.Gui.Pie.AllIcon == "assets/icons/device_add.png")
                    configData.Gui.Pie.AllIcon = "assets/icons/clear.png";
                // CHANGE: 1.6.6 — обратный автофикс 1.6.5: у контурного circle_open белая
                // CHANGE: заливка (тонируется только кольцо), возвращаем сплошной кружок.
                if (configData.Skins.ColourIcon == "assets/icons/circle_open.png")
                    configData.Skins.ColourIcon = "assets/icons/circle_closed.png";
                SaveConfig();
            }
            catch
            {
                PrintError("Файл конфигурации поврежден или отсутствует. Создаем новый.");
                LoadDefaultConfig();
                SaveConfig();
            }
        }

        /// <inheritdoc />
        protected override void LoadDefaultConfig()
        {
            PrintWarning("Создание нового файла конфигурации...");
            configData = new ConfigData();
        }

        /// <inheritdoc />
        protected override void SaveConfig() => Config.WriteObject(configData);

        /// <summary>
        /// Переносит конфигурацию предыдущей версии: единый раздел «Основные» разбит на
        /// «Настройки ремува» и «Настройки апгрейда». Значения копируются 1:1 (лишние для
        /// каждого раздела поля молча игнорируются Newtonsoft при последующей десериализации),
        /// после чего устаревший раздел удаляется из файла.
        /// Предусловие: файл конфигурации существует и читается как JSON.
        /// Постусловие: при отсутствии раздела «Основные» файл не модифицируется.
        /// Сложность: O(1) — размер дерева фиксирован числом настроек конфигурации.
        /// </summary>
        private void MigrateLegacyConfig()
        {
            // CHANGE: Newtonsoft игнорирует неизвестные свойства — без миграции настройки уже
            // CHANGE: работающих серверов (проценты, привилегии, чёрный список) слетели бы на дефолты.
            if (Config.ReadObject<JObject>() is not { } root)
                return;
            if (root["Основные"] is not { Type: JTokenType.Object } legacy)
                return;
            // CHANGE: DeepClone обязателен: один JToken не может принадлежать двум разделам дерева.
            root["Настройки ремува"] ??= legacy.DeepClone();
            root["Настройки апгрейда"] ??= legacy.DeepClone();
            root.Remove("Основные");
            Config.WriteObject(root);
        }

        /// <summary>
        /// Миграция чёрного списка. Конфиги до 1.6.3 содержат только пустую строку ([""]) —
        /// такой список заменяется дефолтом; список, в точности равный дефолту 1.6.3
        /// (LegacyBlockedShortnames), канонически обновляется до текущего дефолта
        /// (small.oil.refinery → refinery_small_deployed). Пользовательские списки (не равные
        /// ни одному из дефолтов) не трогаются. Внимание: полностью очищенный список вернётся
        /// к дефолту при перезагрузке — «очищено» и «старый дефолт» в файле неразличимы
        /// (NormalizeBlockedShortnames сохраняет пустой список как [""]).
        /// Предусловие: configData десериализован; вызывается до NormalizeBlockedShortnames.
        /// Постусловие: в списке есть хотя бы одна непустая запись.
        /// Сложность: O(n), n — записей списка.
        /// </summary>
        private void MigrateBlockedShortnames()
        {
            var entries = configData.Remove.BlockedShortnames
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .ToList();
            if (
                entries.Count == 0
                || (
                    entries.Count == RemoveSettings.LegacyBlockedShortnames.Length
                    && !entries.Except(
                            RemoveSettings.LegacyBlockedShortnames,
                            StringComparer.OrdinalIgnoreCase)
                        .Any()
                )
            )
            {
                configData.Remove.BlockedShortnames =
                    new List<string>(RemoveSettings.DefaultBlockedShortnames);
            }
        }

        /// <summary>
        /// Чистит чёрный список shortname от пустых и дублирующихся записей.
        /// Постусловие: список не пуст — при полном отсутствии записей остается одна пустая строка "".
        /// </summary>
        private void NormalizeBlockedShortnames()
        {
            var entries = new List<string>();
            foreach (var raw in configData.Remove.BlockedShortnames)
            {
                var entry = raw?.Trim();
                if (string.IsNullOrEmpty(entry) || entries.Contains(entry))
                    continue;
                entries.Add(entry);
            }
            if (entries.Count == 0)
                entries.Add(string.Empty);
            configData.Remove.BlockedShortnames = entries;
        }

        #endregion Конфигурация

        #region Поля и константы

        private const string PermUse = "rremove.use";
        private const string PermAdmin = "rremove.admin";
        private const string PermUpgrade = "rremove.upgrade";
        private const string PermNoCost = "rremove.nocost";

        // CHANGE: Привилегия бесплатного использования скинов построек (обход проверки владения DLC).
        private const string PermSkinsFree = "rremove.skinsfree";

        // CHANGE: Ванильный источник списка скинов построек — префаб, чьи ConstructionGrade
        // CHANGE: перечисляет Admin.FindBuildingSkin (Admin.FindBuildingSkin в декомпиле,
        // CHANGE: Assembly-CSharp_chunk25:17582); по нему игра сама определяет канонический
        // CHANGE: набор пар (тип апгрейда, Steam ID скина).
        private const uint SkinListPrefabId = 2194854973u;
        private const string UiRoot = "RRemove_Root";
        private const string UiTimerLabel = "RRemove_Timer";
        private const string UiGradeLabel = "RRemove_Grade";

        // CHANGE: Единый путь data-файла (RSystem/RRemove/RRemove.json) — читается и пишется только через него.
        private const string DataFile = "RSystem/RRemove/RRemove";

        private enum RemoveMode
        {
            Default,
            Admin,
            All,
        }

        private sealed class RemoveState
        {
            public RemoveMode Mode;
            public float ExpiresAt;
        }

        // CHANGE: Состояние режима апгрейда: тип блока + скин постройки + момент истечения бездействия.
        private sealed class UpgradeState
        {
            public BuildingGrade.Enum Grade;

            // CHANGE: Выбранный скин постройки (Steam item definition ID, 0 — стандартный):
            // CHANGE: применяется и к автоапгрейду планом, и к ударам киянкой.
            public ulong Skin;

            // CHANGE: 1.6.0 — выбранный в цветовом меню индекс цвета палитры (1-основной,
            // CHANGE: 0 — случайный цвет, ванильная семантика GetStartingDetailColour(0)).
            public uint Colour;
            public float ExpiresAt;
        }

        // Инвариант: ключ — userID игрока; состояние существует только пока режим активен.
        private readonly Dictionary<ulong, RemoveState> activeRemovers = new();

        // CHANGE: Режим автоапгрейда: ключ — userID, значение — выбранный в меню тип блока.
        // CHANGE: Взаимоисключающ с activeRemovers: включение апгрейда гасит режим удаления и наоборот.
        private readonly Dictionary<ulong, UpgradeState> upgradeGrades = new();

        // CHANGE: Скины построек: выбранный игроком скин на КАЖДЫЙ тип апгрейда (ключ — userID).
        // CHANGE: Хранится в памяти до рестарта: скин — временное предпочтение, расширять
        // CHANGE: data-файл (knownUsers) ради него не нужно.
        private readonly Dictionary<ulong, Dictionary<BuildingGrade.Enum, ulong>> selectedSkins = new();

        // CHANGE: Реестр игровых DLC-скинов построек, сгруппированный по типу апгрейда
        // CHANGE: (источник — ванильный SkinListPrefabId).
        private readonly Dictionary<BuildingGrade.Enum, List<SkinInfo>> gameSkinRegistry = new();

        // CHANGE: Плоская карта «ID скина → английское имя из игровых данных»: источник
        // CHANGE: ключей локализации Skin{ID} и fallback имени без перебора реестра. O(1).
        private readonly Dictionary<ulong, string> skinNamesById = new();

        // CHANGE: 1.6.0 — реестр палитр скинов с поддержкой цвета (колесо баллончика):
        // CHANGE: ключ — Steam item definition ID скина, значение — цвета палитры
        // CHANGE (ConstructionSkin_CustomDetail.ColourLookup.AllColours игровых данных).
        private readonly Dictionary<ulong, Color[]> colourPalettes = new();

        // CHANGE: 1.6.0 — выбранный игроком цвет палитры на каждый тип апгрейда (ключ — userID,
        // CHANGE: значение — 1-основной индекс цвета, 0 — случайный). Живёт в памяти до рестарта.
        private readonly Dictionary<ulong, Dictionary<BuildingGrade.Enum, uint>> selectedColours = new();

        // CHANGE: 1.6.0 — тип апгрейда, ожидающий выбора цвета (между открытием цветового
        // CHANGE: меню и выбором цвета). Инвариант: запись существует только пока открыто меню.
        private readonly Dictionary<ulong, BuildingGrade.Enum> pendingColourSelections = new();

        // CHANGE: Игровой скин постройки: BuildingGrade.skin — это Steam item definition ID
        // CHANGE: (владение проверяется по Steam-инвентарю игрока), alwaysUnlock — ванильный
        // CHANGE: обход проверки владения, EnglishName — английское имя из upgradeMenu.
        private sealed class SkinInfo
        {
            public ulong SkinId;
            public bool AlwaysUnlock;
            public string EnglishName = string.Empty;

            // CHANGE: 1.6.0 — ванильный флаг BuildingGrade.supportsColourChange: скин имеет
            // CHANGE: палитру цветов (ConstructionSkin_CustomDetail + ColourLookup).
            public bool SupportsColourChange;
        }

        // CHANGE: Кэш ссылок на плагины рейд/комбат-блока; ищутся лениво (плагины могут грузиться позже нас).
        private Plugin? raidBlockPlugin;
        private Plugin? combatBlockPlugin;

        // CHANGE: Встроенная система блокировки (активна при false у флагов конфига):
        // CHANGE: ключ — userID, значение — момент снятия блокировки (Time.realtimeSinceStartup).
        private readonly Dictionary<ulong, float> builtinRaidBlocked = new();
        private readonly Dictionary<ulong, float> builtinCombatBlocked = new();

        // Инвариант: ключ — NetworkableId сущности; время её установки (только что поставленные объекты).
        private readonly Dictionary<NetworkableId, float> placedTimes = new();

        // CHANGE: Кэш «prefabID энтити → ItemDefinition» через ItemModDeployable.entityPrefab:
        // CHANGE: у DLC-объектов shortname энтити не совпадает с shortname предмета,
        // CHANGE: поэтому FindItemDefinition(ShortPrefabName) их не находит.
        private readonly Dictionary<uint, ItemDefinition> deployableItems = new();

        // Инвариант: userID игроков, уже использовавших режим удаления; подсказка о бинде им не показывается.
        // Изменения сохраняются в data-файле — знание переживает переподключения и рестарт сервера.
        private HashSet<ulong> knownUsers = new();

        // CHANGE: Тикер не сохраняется в поле: тип Timer из Oxide.Core недоступен при нативной
        // CHANGE: компиляции Carbon, а таймеры плагина Carbon/Oxide убивают автоматически при выгрузке.

        // CHANGE: Нормализованная кнопка открытия меню (0 — OFF, 1 — X, 2 — F). Кэшируется при старте:
        // хук OnServerCommand срабатывает на КАЖДУЮ консольную команду сервера, и аллокация строк
        // (Trim/ToUpperInvariant) на каждый вызов давила на GC и роняла FPS.
        private int menuOpenButton = 0;

        #endregion Поля и константы

        #region Инициализация

        /// <summary>Регистрирует права, запускает секундный тикер обновления GUI.</summary>
        private void OnServerInitialized()
        {
            // CHANGE: Одноразовая нормализация кнопки открытия меню (без аллокаций в горячих хуках)
            menuOpenButton =
                configData.Remove.MenuOpenButton?.Trim().ToUpperInvariant() switch
                {
                    "X" => 1,
                    "F" => 2,
                    _ => 0,
                };

            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermUpgrade, this);
            permission.RegisterPermission(PermNoCost, this);
            // CHANGE: Привилегия бесплатных скинов построек (обход проверки владения DLC) берётся из конфига.
            permission.RegisterPermission(configData.Skins.FreeSkinPermission, this);
            permission.RegisterPermission(configData.Remove.IgnorePermission, this);
            // CHANGE: Подсказка о бинде показывается до первого использования режима — факт
            // использования сохраняется в data-файле и переживает рестарты.
            knownUsers =
                Interface.Oxide.DataFileSystem.ReadObject<HashSet<ulong>>(DataFile)
                ?? new HashSet<ulong>();
            BuildDeployableItemCache();
            // CHANGE: Реестр скинов построек строится после загрузки конфига: автозаполнение
            // CHANGE: списков скинов найденными в игре DLC-скинами + сохранение дополненного конфига.
            BuildSkinRegistry();
            // CHANGE: 1.6.0 — реестр палитр скинов с поддержкой цвета (колесо баллончика).
            BuildColourPaletteRegistry();
            // CHANGE: Имена скинов — ключи локализации Skin{ID}: регистрируются из игровых
            // CHANGE: данных при каждом старте и редактируются сервером в lang-файлах.
            RegisterSkinNameMessages();
            SyncSkinConfig();
            timer.Every(1f, UpdateTickers);

            // CHANGE: Встроенная система блокировки (OnEntityTakeDamage) нужна только когда
            // CHANGE: хотя бы один из запретов работает по ней (false в конфиге).
            if (configData.Remove.BlockInRaidZone && configData.Remove.BlockInCombatBlock)
                Unsubscribe(nameof(OnEntityTakeDamage));
        }

        /// <summary>Гасит GUI и состояние всех игроков (таймеры плагина снимаются автоматически).</summary>
        private void Unload()
        {
            foreach (var userId in activeRemovers.Keys.ToArray())
                Deactivate(userId);
            foreach (var userId in upgradeGrades.Keys.ToArray())
                DeactivateUpgrade(userId);
            // CHANGE: 1.6.2 — заглушка открытых pie (ReplaceOpenPiesWithStub) удалена.
            // CHANGE: Принудительно закрыть pie с сервера нельзя (RPC закрытия нет —
            // CHANGE: CommunityEntity.Pie.cs, OpenPie только открывает), а трекинг «вероятно
            // CHANGE: открытых» колёс лгал: ESC закрывает pie без всякого серверного события,
            // CHANGE: поэтому после перезагрузки плагина заглушка САМА открывала белое меню
            // CHANGE: с единственной «Закрыть» на пустом экране. Открытое колесо теперь
            // CHANGE: переживает перезагрузку без замены: команды перерегистрируются теми же
            // CHANGE: именами при загрузке плагина, а вовсе без плагина колесо закрывается
            // CHANGE: первым же кликом по любому пункту (клиент закрывает pie при исполнении
            // CHANGE: команды пункта — паттерн ванильного pietest).
        }

        /// <summary>Сбрасывает режим игрока при выходе (GUI исчезает вместе с клиентом).</summary>
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
            {
                Deactivate(player.userID, destroyUi: false);
                DeactivateUpgrade(player.userID, destroyUi: false);
                // CHANGE: 1.6.0 — незавершённый выбор цвета и выбранные цвета больше не живут
                // CHANGE: после выхода игрока (состояние — только пока игрок подключен).
                pendingColourSelections.Remove(player.userID);
                selectedColours.Remove(player.userID);
            }
        }

        /// <summary>Запоминает время установки новой постройки/предмета для лимита времени удаления.</summary>
        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var entity = go?.GetComponent<BaseEntity>();
            if (entity?.net == null)
                return;
            placedTimes[entity.net.ID] = Time.realtimeSinceStartup;

            // CHANGE: Автоапгрейд: строительный план ставит твинь — сразу поднимаем блок
            // CHANGE: до типа, выбранного игроком в радиальном меню.
            if (
                configData.Upgrade.EnableAutoUpgrade
                && entity is BuildingBlock block
                && plan.GetOwnerPlayer() is { } builder
                && upgradeGrades.TryGetValue(builder.userID, out var upgradeState)
            )
            {
                // CHANGE: В рейд/комбат-блоке автоапгрейд постройки запрещён (конфиг).
                var buildDeny = GetBlockDenyReason(builder, out var buildLeft);
                if (buildDeny != null)
                {
                    DenyToast(builder, buildDeny, buildLeft);
                    return;
                }
                UpgradeBlock(builder, block, upgradeState);
            }
        }

        /// <summary>
        /// Встроенная система блокировки (работает при false у флагов конфига):
        /// взрыв по строению — рейдер (инициатор) и владелец получают рейд-блок;
        /// ПВП-урон между игроками — обе стороны получают комбат-блок.
        /// Длительности — из конфига (по умолчанию 5 минут).
        /// Сложность: O(1) на вызов.
        /// </summary>
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info?.InitiatorPlayer is not { } attacker || entity == null || attacker.IsNpc)
                return;

            var now = Time.realtimeSinceStartup;

            // Взрыв по любой постройке (включая свою) — рейд-блок инициатору и владельцу.
            // CHANGE: Запись и уведомление — только когда встроенная система активна (флаг false).
            if (
                !configData.Remove.BlockInRaidZone
                && info.damageTypes != null
                && info.damageTypes.Get(Rust.DamageType.Explosion) > 0f
                && entity is StabilityEntity
            )
            {
                var raidDuration = Mathf.Max(1, configData.Remove.BuiltinRaidBlockDuration);
                builtinRaidBlocked[attacker.userID] = now + raidDuration;
                if (entity.OwnerID != 0u && entity.OwnerID != attacker.userID)
                    builtinRaidBlocked[entity.OwnerID] = now + raidDuration;

                // CHANGE: GameTip «Инициализировался рейд» — рейдеру и владельцу (если онлайн).
                NotifyRaidInitialized(attacker, raidDuration);
                if (entity.OwnerID != 0u && BasePlayer.FindByID(entity.OwnerID) is { } owner && owner.userID != attacker.userID)
                    NotifyRaidInitialized(owner, raidDuration);
            }

            // ПВП-урон — комбат-блок обеим сторонам (только при встроенной системе).
            if (
                !configData.Remove.BlockInCombatBlock
                && entity is BasePlayer victim
                && !victim.IsNpc
                && victim.userID != attacker.userID
            )
            {
                var until = now + Mathf.Max(1, configData.Remove.BuiltinCombatBlockDuration);
                builtinCombatBlocked[attacker.userID] = until;
                builtinCombatBlocked[victim.userID] = until;
            }
        }

        /// <summary>GameTip «Инициализировался рейд» с длительностью из конфига ({0} — секунды).</summary>
        private void NotifyRaidInitialized(BasePlayer player, int duration) =>
            Toast(player, Msg(player, "RaidInitialized").Replace(
                "{0}", duration.ToString(CultureInfo.InvariantCulture)));

        /// <summary>Чистит время установки уничтоженных сущностей (защита от роста словаря).</summary>
        private void OnEntityDestroyed(NetworkableId id) => placedTimes.Remove(id);

        /// <summary>
        /// Подсказка с готовой командой бинда клавиши активации.
        /// Показывается при каждом взятии в руки киянки (hammer/toolgun — оба вида в Rust),
        /// но только пока игрок ни разу не воспользовался режимом удаления
        /// (сервер не может проверить наличие клиентского бинда, поэтому сигнал — использование режима).
        /// </summary>
        /// <summary>
        /// Подсказка об открытии радиального меню при взятии в руки киянки, пистолета-строителя (toolgun)
        /// или плана постройки (planner).
        /// X/F (открытие по кнопке, без бинда) — показывается при КАЖДОМ взятии инструмента в руки:
        /// бинд не требуется, скрывать подсказку после первого использования не нужно.
        /// OFF (активация по бинду) — только пока игрок ни разу не воспользовался режимом удаления
        /// (сервер не может проверить наличие клиентского бинда, сигнал — использование режима).
        /// </summary>
        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player == null || !player.IsConnected || !configData.Remove.ShowBindHint)
                return;
            if (newItem?.info == null)
                return;

            // CHANGE: menuOpenButton кэшируется при старте — без аллокаций строк в хуке
            bool openByButton = menuOpenButton != 0;

            // Режим OFF: подсказка про бинд нужна только до первого использования режима.
            if (!openByButton && knownUsers.Contains(player.userID))
                return;

            var shortname = newItem.info.shortname;
            if (
                !shortname.Equals("hammer", StringComparison.Ordinal)
                && !shortname.Equals("toolgun", StringComparison.Ordinal)
                && !shortname.Equals("planner", StringComparison.Ordinal)
            )
                return;

            player.Command(
                "gametip.showtoast",
                0,
                GetBindHintMessage(player));
        }

        /// <summary>
        /// Текст подсказки об открытии радиального меню:
        /// X/F — «нажмите кнопку» (открытие без бинда через перехват команды);
        /// OFF — готовая команда бинда rremove.menu для консоли.
        /// </summary>
        private string GetBindHintMessage(BasePlayer player)
        {
            if (menuOpenButton == 1)
                return Msg(player, "MenuOpenHint").Replace("{key}", "X");
            if (menuOpenButton == 2)
                return Msg(player, "MenuOpenHint").Replace("{key}", "F");
            return Msg(player, "BindHint").Replace("{key}", configData.Remove.ActivationKey);
        }

        #endregion Инициализация

        #region Локализация

        /// <inheritdoc />
        protected override void LoadDefaultMessages()
        {
            // CHANGE: 1.5.0 — английский словарь переведён на английский (раньше был копией русского).
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["NoPermission"] = "You don't have permission to use this command.",
                    ["BindHint"] = "Remove mode: run this once in the console (F1): bind {key} rremove.menu",
                    ["MenuOpenHint"] = "Remove/upgrade mode: press [{key}] to open the radial menu",
                    ["BlockedShortname"] = "This entity is blocked from removal.",
                    ["CannotRemovePlayers"] = "Players cannot be removed.",
                    ["OwnNoCupboard"] = "No tool cupboard nearby: removing your own entities requires cupboard authorization.",
                    ["OwnNotAuthed"] = "This is your entity, but you are not authorized in this building's tool cupboard.",
                    ["OthersRemovalDisabled"] = "This entity belongs to another player. You are authorized in the cupboard, but removing others' entities is disabled in the server settings.",
                    ["TeamRemovalDisabled"] = "This entity belongs to your teammate, but teammate removal is disabled in the server settings.",
                    ["ClanRemovalDisabled"] = "This entity belongs to your clanmate, but clanmate removal is disabled in the server settings.",
                    ["NotOwner"] = "This entity belongs to another player, and you are not allowed to remove it.",
                    ["ContainerNotEmpty"] = "Cannot remove: the entity contains items.",
                    ["QuarryRunning"] = "Cannot remove: the quarry is running. Turn it off first.",
                    ["QuarryHasFuel"] = "Cannot remove: the quarry's fuel tank still has diesel. Take it out before removal.",
                    ["QuarryFuelNotSpent"] = "Cannot remove: the diesel in the quarry is not fully spent yet. Let the quarry finish its work.",
                    ["QuarryLocked"] = "Cannot remove: a lock is installed on the quarry.",
                    ["PlaceTimeLimit"] = "This entity can no longer be removed: the removal time limit has expired.",
                    ["AlreadyDestroyed"] = "The entity is already destroyed.",
                    ["TimeLeft"] = "Time left: {0} s",
                    ["GuiTitle"] = "REMOVE MODE",
                    ["GuiAdminTitle"] = "ADMIN REMOVE",
                    ["GuiAllTitle"] = "REMOVE BUILDING",
                    ["GuiHint"] = "Hit an entity with the hammer to remove it",
                    ["NoBuilding"] = "This entity is not attached to a building.",
                    ["PieDefault"] = "Simple remove",
                    ["PieDefaultDesc"] = "Remove your own entities with a resource refund",
                    ["PieAdmin"] = "Admin remove",
                    ["PieAdminDesc"] = "Remove any entities without ownership checks",
                    ["PieAll"] = "Remove building",
                    ["PieAllDesc"] = "Remove every entity connected to this building",
                    ["PieClose"] = "Close",
                    ["PieCloseDesc"] = "Exit the menu without choosing a mode",
                    ["PieWood"] = "Wood",
                    ["PieWoodDesc"] = "Auto-upgrade the building to wood",
                    ["PieStone"] = "Stone",
                    ["PieStoneDesc"] = "Auto-upgrade the building to stone",
                    ["PieMetal"] = "Metal",
                    ["PieMetalDesc"] = "Auto-upgrade the building to metal",
                    ["PieHqm"] = "HQM",
                    ["PieHqmDesc"] = "Auto-upgrade the building to high quality metal",
                    ["GuiUpgradeTitle"] = "UPGRADE MODE",
                    ["GuiUpgradeHint"] = "Hit with the hammer or build with the plan",
                    ["GradeWood"] = "Wood",
                    ["GradeStone"] = "Stone",
                    ["GradeMetal"] = "Metal",
                    ["GradeHqm"] = "HQM",
                    ["UpgradeNoResources"] = "Not enough resources for the upgrade.",
                    ["UpgradeNoBuildAuth"] = "You are not authorized to build here.",
                    ["UpgradeNoCupboard"] = "Auto-upgrade requires authorization in this building's tool cupboard.",
                    ["RaidBlockDenied"] = "You are raid-blocked: the radial menu, remove and upgrade are unavailable. Time left: {0} s",
                    ["CombatBlockDenied"] = "You are combat-blocked: the radial menu, remove and upgrade are unavailable. Time left: {0} s",
                    ["RaidInitialized"] = "Raid initialized — the radial menu, remove and upgrade are unavailable for {0} s",
                    ["PieSkinNone"] = "No skin",
                    ["PieSkinCurrent"] = "Skin: {0}",
                    ["SkinNotOwned"] = "This skin is unavailable: the DLC purchase is required.",
                    ["SkinFallback"] = "The skin is unavailable for this building — the default one is applied.",
                    // CHANGE: 1.6.0 — цветовое колесо (палитра скина-контейнера) перед запуском режима апгрейда.
                    // CHANGE: 1.6.1 — нумерация цветов убрана (ключ PieColourOption удалён):
                    // CHANGE: у колеса баллончика номеров нет, пункт называется просто «Цвет».
                    ["PieColourSwatch"] = "Colour",
                    ["PieColourRandom"] = "Random colour",
                    ["PieColourRandomDesc"] = "Each upgraded block gets a random palette colour",
                    ["PieColourCancelDesc"] = "Cancel: the upgrade mode will not start",
                },
                this,
                "en"
            );
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["NoPermission"] = "У вас нет прав на использование этой команды.",
                    ["BindHint"] = "Режим удаления: выполните один раз в консоли (F1): bind {key} rremove.menu",
                    ["MenuOpenHint"] = "Режим удаления/апгрейда: нажмите [{key}] для открытия меню",
                    ["BlockedShortname"] = "Этот объект запрещён к удалению.",
                    ["CannotRemovePlayers"] = "Игроков удалять нельзя.",
                    ["OwnNoCupboard"] = "Рядом нет шкафа: удаление своих объектов требует авторизации в шкафу.",
                    ["OwnNotAuthed"] = "Это ваш объект, но вы не авторизованы в шкафу этого строения.",
                    ["OthersRemovalDisabled"] = "Объект чужой, вы авторизованы в шкафу, но удаление чужих объектов отключено в настройках сервера.",
                    ["TeamRemovalDisabled"] = "Это объект товарища по команде, но удаление объектов команды отключено в настройках сервера.",
                    // CHANGE: Отдельная причина для клана (клановый стол) — раньше соклановцы
                    // CHANGE: получали сообщение про команду (Team), хотя системы разные.
                    ["ClanRemovalDisabled"] = "Это объект соклановца, но удаление объектов клана отключено в настройках сервера.",
                    ["NotOwner"] = "Это чужой объект, и у вас нет доступа к его удалению.",
                    ["ContainerNotEmpty"] = "Нельзя удалить: в объекте есть предметы.",
                    ["QuarryRunning"] = "Нельзя удалить: карьер работает. Сначала выключите его.",
                    ["QuarryHasFuel"] = "Нельзя удалить: в топливном баке карьера есть дизель. Заберите его перед удалением.",
                    ["QuarryFuelNotSpent"] = "Нельзя удалить: дизель в карьере не израсходован полностью. Дайте карьеру доработать.",
                    ["QuarryLocked"] = "Нельзя удалить: на карьере установлен замок.",
                    ["PlaceTimeLimit"] = "Этот объект уже нельзя удалить: истекло время на удаление.",
                    ["AlreadyDestroyed"] = "Объект уже уничтожен.",
                    ["TimeLeft"] = "Осталось: {0} с",
                    ["GuiTitle"] = "РЕЖИМ УДАЛЕНИЯ",
                    ["GuiAdminTitle"] = "АДМИНСКОЕ УДАЛЕНИЕ",
                    ["GuiAllTitle"] = "УДАЛЕНИЕ СТРОЕНИЯ",
                    ["GuiHint"] = "Ударьте молотком по объекту для удаления",
                    ["NoBuilding"] = "Этот объект не привязан к строению.",
                    ["PieDefault"] = "Простой ремув",
                    ["PieDefaultDesc"] = "Удаление своих объектов с возвратом ресурсов",
                    ["PieAdmin"] = "Админский ремув",
                    ["PieAdminDesc"] = "Удаление любых объектов без проверок владения",
                    ["PieAll"] = "Удаление строения",
                    ["PieAllDesc"] = "Удалить все связанные объекты строения",
                    ["PieClose"] = "Закрыть",
                    ["PieCloseDesc"] = "Выйти из меню без выбора режима",
                    ["PieWood"] = "Дерево",
                    ["PieWoodDesc"] = "Автоапгрейд постройки в дерево",
                    ["PieStone"] = "Камень",
                    ["PieStoneDesc"] = "Автоапгрейд постройки в камень",
                    ["PieMetal"] = "Металл",
                    ["PieMetalDesc"] = "Автоапгрейд постройки в металл",
                    ["PieHqm"] = "МВК",
                    ["PieHqmDesc"] = "Автоапгрейд постройки в МВК",
                    ["GuiUpgradeTitle"] = "РЕЖИМ АПГРЕЙДА",
                    ["GuiUpgradeHint"] = "Бейте киянкой или стройте планом",
                    ["GradeWood"] = "Дерево",
                    ["GradeStone"] = "Камень",
                    ["GradeMetal"] = "Металл",
                    ["GradeHqm"] = "МВК",
                    ["UpgradeNoResources"] = "Недостаточно ресурсов для апгрейда.",
                    ["UpgradeNoBuildAuth"] = "Вы не авторизованы для строительства здесь.",
                    ["UpgradeNoCupboard"] = "Для автоапгрейда нужна авторизация в шкафу этого строения.",
                    ["RaidBlockDenied"] = "Вы в рейд-блоке: радиальное меню, ремув и апгрейд недоступны. Осталось: {0} с",
                    ["CombatBlockDenied"] = "Вы в комбат-блоке: радиальное меню, ремув и апгрейд недоступны. Осталось: {0} с",
                    ["RaidInitialized"] = "Инициализировался рейд — радиальное меню, ремув и апгрейд недоступны в течение {0} с",
                    ["PieSkinNone"] = "Без скина",
                    // CHANGE: строка текущего скина в описании типа апгрейда (ванильный пейджер Q/E).
                    ["PieSkinCurrent"] = "Скин: {0}",
                    ["SkinNotOwned"] = "Этот скин недоступен: требуется покупка DLC.",
                    ["SkinFallback"] = "Скин недоступен для этой постройки — применён стандартный.",
                    // CHANGE: 1.6.0 — цветовое колесо (палитра скина-контейнера) перед запуском режима апгрейда.
                    // CHANGE: 1.6.1 — нумерация цветов убрана (ключ PieColourOption удалён):
                    // CHANGE: у колеса баллончика номеров нет, пункт называется просто «Цвет».
                    ["PieColourSwatch"] = "Цвет",
                    ["PieColourRandom"] = "Случайный цвет",
                    ["PieColourRandomDesc"] = "Каждый апгрейдный блок получает случайный цвет палитры",
                    ["PieColourCancelDesc"] = "Отмена: режим апгрейда не запустится",
                },
                this,
                "ru"
            );
        }

        /// <summary>
        /// Возвращает локализованное сообщение для игрока.
        /// Инвариант: результат не null — при отсутствии перевода возвращается сам ключ.
        /// </summary>
        private string Msg(BasePlayer? player, string key) =>
            lang.GetMessage(key, this, player?.UserIDString) ?? key;

        #endregion Локализация

        /// <summary>
        /// Проверяет рейд/комбат блок игрока. Источник зависит от конфига:
        /// true — API внешнего плагина (платный RaidBlock или бесплатный NoEscape),
        /// false — встроенная система RRemove (блок по взрыву/ПВП-урону, длительность в конфиге).
        /// Постусловие: null — блоков нет; строка — ключ причины; secondsLeft — остаток в секундах.
        /// Сложность: O(1).
        /// </summary>
        private string? GetBlockDenyReason(BasePlayer player, out int secondsLeft)
        {
            secondsLeft = 0;

            if (configData.Remove.BlockInRaidZone)
            {
                if (IsApiBlocked(player, RaidBlockPlugin, RaidBlockApiNames))
                {
                    // CHANGE: Платный RaidBlock отдаёт остаток через ApiGetTime; иначе — длительность из конфига.
                    secondsLeft = GetApiBlockTime(player, RaidBlockPlugin)
                        ?? configData.Remove.BuiltinRaidBlockDuration;
                    return "RaidBlockDenied";
                }
            }
            else if (TryGetBuiltinLeft(player.userID, builtinRaidBlocked, out secondsLeft))
                return "RaidBlockDenied";

            if (configData.Remove.BlockInCombatBlock)
            {
                if (IsApiBlocked(player, CombatBlockPlugin, CombatBlockApiNames))
                {
                    secondsLeft = GetApiBlockTime(player, CombatBlockPlugin)
                        ?? configData.Remove.BuiltinCombatBlockDuration;
                    return "CombatBlockDenied";
                }
            }
            else if (TryGetBuiltinLeft(player.userID, builtinCombatBlocked, out secondsLeft))
                return "CombatBlockDenied";

            return null;
        }

        /// <summary>Остаток блокировки из API плагина (ApiGetTime, платный RaidBlock). Постусловие: null — API недоступен.</summary>
        private static int? GetApiBlockTime(BasePlayer player, Plugin? plugin)
        {
            if (plugin != null && plugin.Call("ApiGetTime", player.userID) is int time)
                return time;
            return null;
        }

        /// <summary>Остаток встроенной блокировки в секундах (с округлением вверх). Постусловие: false — не заблокирован.</summary>
        private static bool TryGetBuiltinLeft(ulong userId, Dictionary<ulong, float> blocked, out int secondsLeft)
        {
            if (blocked.TryGetValue(userId, out var until) && until > Time.realtimeSinceStartup)
            {
                secondsLeft = Mathf.CeilToInt(until - Time.realtimeSinceStartup);
                return true;
            }
            secondsLeft = 0;
            return false;
        }

        /// <summary>GameTip-уведомление вместо чата (не спамит чат). style: 0 — инфо, 1 — ошибка.</summary>
        private void Toast(BasePlayer player, string message, int style = 1) =>
            player.Command("gametip.showtoast", style, message);

        /// <summary>GameTip о запрете с подстановкой остатка времени ({0} — секунды).</summary>
        private void DenyToast(BasePlayer player, string reasonKey, int secondsLeft) =>
            Toast(player, Msg(player, reasonKey).Replace(
                "{0}", secondsLeft.ToString(CultureInfo.InvariantCulture)));

        /// <summary>Удаляет истёкшие записи блокировки. Сложность: O(n), вызывается раз в секунду.</summary>
        private static void PruneExpired(Dictionary<ulong, float> blocked, float now)
        {
            List<ulong>? expired = null;
            foreach (var pair in blocked)
            {
                if (pair.Value <= now)
                    (expired ??= new List<ulong>()).Add(pair.Key);
            }
            if (expired == null)
                return;
            foreach (var userId in expired)
                blocked.Remove(userId);
        }

        private static readonly string[] RaidBlockApiNames = { "IsBlocked", "IsRaidBlocked", "IsRaidBlock" };
        private static readonly string[] CombatBlockApiNames = { "IsCombatBlock", "IsCombatBlocked" };

        // CHANGE: Кэш без ref-параметров: Carbon-компилятор падает на CS1620 при ref с nullable-аннотацией.
        private Plugin? RaidBlockPlugin => raidBlockPlugin ??= plugins.Find("RaidBlock") ?? plugins.Find("NoEscape");
        private Plugin? CombatBlockPlugin => combatBlockPlugin ??= plugins.Find("CombatBlock") ?? plugins.Find("NoEscape");

        /// <summary>Вызывает API блокировки, перебирая имена методов и типы аргументов (BasePlayer/string/ulong).</summary>
        private static bool IsApiBlocked(BasePlayer player, Plugin? plugin, string[] apiNames)
        {
            if (plugin == null)
                return false;
            foreach (var api in apiNames)
            {
                if (plugin.Call(api, player) is bool byPlayer)
                    return byPlayer;
                if (plugin.Call(api, player.UserIDString) is bool byString)
                    return byString;
                if (plugin.Call(api, player.userID) is bool byUlong)
                    return byUlong;
            }
            return false;
        }

        #region Чат-команды

        // CHANGE: Чат-команда /remove удалена — доступ ко всем режимам только через
        // CHANGE: радиальное меню (rremove.menu) и бинд клавиши из конфига.

        /// <summary>Включает режим, если он выключен, и выключает, если включен.</summary>
        private void ToggleMode(BasePlayer player, RemoveMode mode)
        {
            if (activeRemovers.ContainsKey(player.userID))
            {
                // CHANGE: Чат-уведомление о выключении убрано — исчезающая панель и есть сигнал.
                Deactivate(player.userID);
                return;
            }
            Activate(player, mode);
        }

        /// <summary>
        /// Циклическое переключение режимов одной кнопкой (бинд клавиши из конфига).
        /// Админ (rremove.admin): обычный → админ → всё строение → выкл.
        /// Обычный игрок с rremove.use: обычный → выкл. Без прав — отказ.
        /// </summary>
        private void ToggleCycle(BasePlayer player)
        {
            // CHANGE: В рейд/комбат-блоке переключение режимов запрещено (конфиг).
            var toggleDeny = GetBlockDenyReason(player, out var toggleLeft);
            if (toggleDeny != null)
            {
                DenyToast(player, toggleDeny, toggleLeft);
                return;
            }

            var admin = IsAdmin(player);
            if (!admin && !permission.UserHasPermission(player.UserIDString, PermUse))
            {
                Toast(player, Msg(player, "NoPermission"));
                return;
            }

            if (!activeRemovers.TryGetValue(player.userID, out var state))
            {
                Activate(player, RemoveMode.Default);
                return;
            }

            var next = NextCycleMode(state.Mode, admin);
            if (next == null)
            {
                Deactivate(player.userID);
                return;
            }
            Deactivate(player.userID);
            Activate(player, next.Value);
        }

        /// <summary>
        /// Следующий режим цикла. Постусловие: null — выключить режим; иначе — режим для активации.
        /// </summary>
        private static RemoveMode? NextCycleMode(RemoveMode current, bool admin) =>
            current switch
            {
                RemoveMode.Default => admin ? RemoveMode.Admin : null,
                RemoveMode.Admin => RemoveMode.All,
                _ => null,
            };

        /// <summary>
        /// Консольная команда rremove.toggle — назначается на клавишу из конфига биндом
        /// bind &lt;клавиша&gt; rremove.toggle и циклически переключает режимы удаления.
        /// </summary>
        [ConsoleCommand("rremove.toggle")]
        private void ConsoleCmdToggle(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !player.IsConnected)
                return;
            ToggleCycle(player);
        }

        /// <summary>
        /// rremove.menu — открывает нативное клиентское pie-меню (CustomPie) с выбором режима удаления.
        /// Назначается на клавишу из конфига: bind &lt;клавиша&gt; rremove.menu.
        /// </summary>
        [ConsoleCommand("rremove.menu")]
        private void ConsoleCmdRadialMenu(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null || !player.IsConnected)
                return;
            if (!IsAdmin(player) && !permission.UserHasPermission(player.UserIDString, PermUse))
            {
                Toast(player, Msg(player, "NoPermission"));
                return;
            }

            // CHANGE: В рейд/комбат-блоке открытие радиального меню запрещено (конфиг).
            var menuDeny = GetBlockDenyReason(player, out var menuLeft);
            if (menuDeny != null)
            {
                DenyToast(player, menuDeny, menuLeft);
                return;
            }
            SendRadialMenu(player);
        }

        /// <summary>
        /// Перехват клиентской консольной команды кнопки (как в RAdminMenu):
        /// X → vehicle.swapseats, F → inventory.lighttoggle. Открывает радиальное меню без бинда.
        /// Конфиг: RemoveSettings.MenuOpenButton (X | F | OFF). Инвариант: перехват только у игроков
        /// с правом rremove.use/rremove.admin; вне рейд/комбат-блока.
        /// </summary>
        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.cmd == null || menuOpenButton == 0)
                return null;

            string name = arg.cmd.Name;
            string fullName = arg.cmd.FullName;
            bool hit =
                menuOpenButton == 1
                    ? (name == "swapseats" || fullName == "vehicle.swapseats")
                    : (
                        name == "lighttoggle"
                        || fullName == "inventory.lighttoggle"
                        || name == "lighttoggle_sv"
                        || fullName == "inventory.lighttoggle_sv"
                    );
            if (!hit)
                return null;

            var player = arg.Player();
            if (player == null || !player.IsConnected)
                return null;
            if (!IsAdmin(player) && !permission.UserHasPermission(player.UserIDString, PermUse))
                return null;
            if (GetBlockDenyReason(player, out _) != null)
                return null;

            NextTick(() =>
            {
                if (player != null && player.IsConnected)
                    SendRadialMenu(player);
            });
            return true;
        }

        /// <summary>Пункты выбора режима в радиальном меню. Повторный выбор активного режима выключает его.</summary>
        [ConsoleCommand("rremove.mode.default")]
        private void ConsoleCmdModeDefault(ConsoleSystem.Arg arg) => HandleModeSelection(arg, RemoveMode.Default);

        [ConsoleCommand("rremove.mode.admin")]
        private void ConsoleCmdModeAdmin(ConsoleSystem.Arg arg) => HandleModeSelection(arg, RemoveMode.Admin);

        [ConsoleCommand("rremove.mode.all")]
        private void ConsoleCmdModeAll(ConsoleSystem.Arg arg) => HandleModeSelection(arg, RemoveMode.All);

        /// <summary>
        /// Обработчик выбора пункта радиального меню. Предусловие: mode Admin/All — только с правом rremove.admin.
        /// </summary>
        private void HandleModeSelection(ConsoleSystem.Arg arg, RemoveMode mode)
        {
            var player = arg?.Player();
            if (player == null || !player.IsConnected)
                return;
            if (mode == RemoveMode.Default)
            {
                if (!IsAdmin(player) && !permission.UserHasPermission(player.UserIDString, PermUse))
                {
                    Toast(player, Msg(player, "NoPermission"));
                    return;
                }
            }
            else if (!IsAdmin(player))
            {
                Toast(player, Msg(player, "NoPermission"));
                return;
            }

            if (activeRemovers.TryGetValue(player.userID, out var state) && state.Mode == mode)
                Deactivate(player.userID);
            else
            {
                // CHANGE: Режимы взаимоисключающы: включение удаления гасит режим апгрейда.
                DeactivateUpgrade(player.userID);
                Activate(player, mode);
            }
        }

        /// <summary>Пункты выбора типа апгрейда в радиальном меню.</summary>
        [ConsoleCommand("rremove.grade.wood")]
        private void ConsoleCmdGradeWood(ConsoleSystem.Arg arg) => HandleGradeSelection(arg, BuildingGrade.Enum.Wood);

        [ConsoleCommand("rremove.grade.stone")]
        private void ConsoleCmdGradeStone(ConsoleSystem.Arg arg) => HandleGradeSelection(arg, BuildingGrade.Enum.Stone);

        [ConsoleCommand("rremove.grade.metal")]
        private void ConsoleCmdGradeMetal(ConsoleSystem.Arg arg) => HandleGradeSelection(arg, BuildingGrade.Enum.Metal);

        [ConsoleCommand("rremove.grade.hqm")]
        private void ConsoleCmdGradeHqm(ConsoleSystem.Arg arg) => HandleGradeSelection(arg, BuildingGrade.Enum.TopTier);

        /// <summary>
        /// Обработчик выбора типа апгрейда: включает режим; повторный выбор того же типа выключает.
        /// Предусловие: право rremove.upgrade (или rremove.admin).
        /// </summary>
        private void HandleGradeSelection(ConsoleSystem.Arg arg, BuildingGrade.Enum grade)
        {
            var player = arg?.Player();
            if (player == null || !player.IsConnected)
                return;
            if (!configData.Upgrade.EnableAutoUpgrade)
                return;
            // CHANGE: Автоапгрейд — только по своей привилегии rremove.upgrade; rremove.admin
            // CHANGE: даёт доступ лишь к админскому удалению, апгрейд не выдаёт.
            if (!permission.UserHasPermission(player.UserIDString, PermUpgrade))
            {
                Toast(player, Msg(player, "NoPermission"));
                return;
            }

            if (activeRemovers.ContainsKey(player.userID))
                Deactivate(player.userID);

            if (upgradeGrades.TryGetValue(player.userID, out var current) && current.Grade == grade)
            {
                DeactivateUpgrade(player.userID);
                return;
            }
            // CHANGE: 1.6.0 — если у выбранного скина есть палитра (контейнеры), перед
            // CHANGE: активацией режима (до появления таймера) открывается радиальное меню
            // CHANGE: выбора цвета — как колесо баллончика; иначе режим запускается сразу.
            if (!TryOpenColourSelection(player, grade))
                ActivateUpgrade(player, grade);
        }

        #region Выбор цвета (колесо баллончика, 1.6.0)

        /// <summary>
        /// Пытается открыть радиальное меню выбора цвета скина с палитрой перед активацией
        /// режима апгрейда. Меню — нативное pie-меню клиента: каждый цвет — отдельный пункт
        /// с закрашенным кружком (CustomPieMenu.color + colorMode), выбор исполняет команду
        /// пункта как есть — индекс цвета зашит в саму команду (rremove.colour.&lt;тип&gt; N).
        /// Инвариант: клиент раскладывает пункты pie по часовой стрелке в порядке списка,
        /// начиная с 12 часов (проверено на колесе 1.6.0: список [Цвет N..Цвет 1, Закрыть,
        /// Случайный] лёг по часовой с «Цвет N» на 12 часах), поэтому палитра добавляется
        /// в естественном порядке 1..N: цвет N замыкает круг перед «Закрыть»,
        /// «Случайный» граничит с цветом 1.
        /// Предусловия: игрок подключен; grade — тип апгрейда из меню.
        /// Постусловие: true — меню отправлено, активация режима отложена до выбора цвета
        /// (запись в pendingColourSelections); false — палитры нет/функция выключена,
        /// режим нужно активировать немедленно.
        /// Сложность: O(k), k — размер палитры (до 16 цветов ванили).
        /// </summary>
        private bool TryOpenColourSelection(BasePlayer player, BuildingGrade.Enum grade)
        {
            if (!configData.Skins.EnableColourSelection)
                return false;
            var skinId = GetSelectedSkin(player.userID, grade);
            if (skinId == 0UL || !colourPalettes.TryGetValue(skinId, out var palette))
                return false;
            if (!CanUseBuildingSkin(player, skinId, IsAlwaysUnlocked(grade, skinId)))
                return false;

            pendingColourSelections[player.userID] = grade;
            var pie = Pool.Get<ProtoBuf.CustomPie>();
            try
            {
                pie.menus = Pool.Get<List<ProtoBuf.CustomPieMenu>>();
                var command = $"rremove.colour.{GradeKey(grade)}";
                var skinName = SkinDisplayName(player, skinId);
                // CHANGE: 1.6.1 — палитра добавляется в естественном порядке 1..N: клиент
                // CHANGE: раскладывает пункты pie по часовой стрелке в порядке списка (первый
                // CHANGE: пункт — на 12 часах), цвет N замыкает круг перед «Закрыть» — как у
                // CHANGE: колеса баллончика. Нумерация в названиях убрана: пункт называется
                // CHANGE: просто «Цвет», индекс цвета несёт только команда (rremove.colour.N).
                for (var i = 0; i < palette.Length; i++)
                {
                    if (palette[i] == default)
                        continue;
                    AddPieOption(
                        pie,
                        Msg(player, "PieColourSwatch"),
                        skinName,
                        $"{command} {i + 1}",
                        configData.Skins.ColourIcon,
                        disabled: false,
                        // CHANGE: color != default — клиент строит ColorMode и рисует кружок
                        // выбранного цвета (CommunityEntity.Pie.cs: OpenPie).
                        color: palette[i]);
                }

                // CHANGE: «Закрыть» и «Случайный цвет» стоят после цветов: по часовой стрелке
                // CHANGE: от цвета N идут «Закрыть», затем «Случайный», который граничит
                // CHANGE: с цветом 1 на 12 часах; при выключенном «Случайном» порядок цветов
                // CHANGE: не меняется — замыкает круг «Закрыть».
                AddPieOption(
                    pie,
                    Msg(player, "PieClose"),
                    Msg(player, "PieColourCancelDesc"),
                    string.Empty,
                    configData.Gui.Pie.CloseIcon,
                    disabled: false);

                // CHANGE: Цвет 0 — ванильная семантика «случайный» (GetStartingDetailColour(0)).
                if (configData.Skins.ShowRandomColour)
                    AddPieOption(
                        pie,
                        Msg(player, "PieColourRandom"),
                        Msg(player, "PieColourRandomDesc") + $"\n{skinName}",
                        $"{command} 0",
                        configData.Skins.RandomColourIcon,
                        disabled: false);
                CommunityEntity.ServerInstance.SendPie(player, pie);
            }
            finally
            {
                pie.Dispose();
            }
            return true;
        }

        /// <summary>Команды выбора цвета: rremove.colour.&lt;тип&gt; &lt;индекс|0 — случайный&gt;.</summary>
        [ConsoleCommand("rremove.colour.wood")]
        private void ConsoleCmdColourWood(ConsoleSystem.Arg arg) => HandleColourSelection(arg, BuildingGrade.Enum.Wood);

        [ConsoleCommand("rremove.colour.stone")]
        private void ConsoleCmdColourStone(ConsoleSystem.Arg arg) => HandleColourSelection(arg, BuildingGrade.Enum.Stone);

        [ConsoleCommand("rremove.colour.metal")]
        private void ConsoleCmdColourMetal(ConsoleSystem.Arg arg) => HandleColourSelection(arg, BuildingGrade.Enum.Metal);

        [ConsoleCommand("rremove.colour.hqm")]
        private void ConsoleCmdColourHqm(ConsoleSystem.Arg arg) => HandleColourSelection(arg, BuildingGrade.Enum.TopTier);

        /// <summary>
        /// Обработчик выбора цвета: запоминает цвет типа и активирует режим апгрейда
        /// (теперь появляется таймер режима). Закрытие меню без выбора — отмена активации.
        /// Предусловия: право rremove.upgrade, тип ожидает выбора цвета (pendingColourSelections).
        /// Постусловие: выбран цвет (0 — случайный), режим апгрейда активирован.
        /// Сложность: O(1).
        /// </summary>
        private void HandleColourSelection(ConsoleSystem.Arg arg, BuildingGrade.Enum grade)
        {
            if (arg == null)
                return;
            var player = arg.Player();
            if (player == null || !player.IsConnected)
                return;
            // CHANGE: Меню выбора цвета открывается только из обработчика выбора типа —
            // если запись не найдена (меню протухло/плагин перезагружен), выбор игнорируется.
            if (!pendingColourSelections.Remove(player.userID))
                return;
            if (!permission.UserHasPermission(player.UserIDString, PermUpgrade))
            {
                Toast(player, Msg(player, "NoPermission"));
                return;
            }

            var index = Mathf.Clamp(arg.GetInt(0, 0), 0, int.MaxValue);
            if (!selectedColours.TryGetValue(player.userID, out var prefs))
                selectedColours[player.userID] = prefs = new Dictionary<BuildingGrade.Enum, uint>();
            prefs[grade] = (uint)index;
            ActivateUpgrade(player, grade);
        }

        /// <summary>Выбранный игроком цвет типа. Постусловие: 0 — случайный цвет палитры.</summary>
        private uint GetSelectedColour(ulong userId, BuildingGrade.Enum grade) =>
            selectedColours.TryGetValue(userId, out var prefs) && prefs.TryGetValue(grade, out var colour)
                ? colour
                : 0u;

        #endregion Выбор цвета (колесо баллончика, 1.6.0)

        /// <summary>Включает режим апгрейда: отсчёт таймера от длительности из конфига, GUI с типом и таймером.</summary>
        private void ActivateUpgrade(BasePlayer player, BuildingGrade.Enum grade)
        {
            var duration = Mathf.Max(1f, configData.Upgrade.UpgradeModeDuration);
            upgradeGrades[player.userID] = new UpgradeState
            {
                Grade = grade,
                // CHANGE: Скин берётся из выбора игрока для этого типа апгрейда (0 — стандартный).
                Skin = GetSelectedSkin(player.userID, grade),
                // CHANGE: 1.6.0 — цвет палитры, выбранный в колесе баллончика (0 — случайный).
                Colour = GetSelectedColour(player.userID, grade),
                ExpiresAt = Time.realtimeSinceStartup + duration,
            };
            ShowUpgradeUi(player, grade, Mathf.CeilToInt(duration));
        }

        /// <summary>Выключает режим апгрейда: гасит GUI и снимает состояние.</summary>
        private void DeactivateUpgrade(ulong playerId, bool destroyUi = true)
        {
            if (!upgradeGrades.Remove(playerId))
                return;
            if (destroyUi && BasePlayer.FindByID(playerId) is { } player && configData.Gui.Enabled)
                CuiHelper.DestroyUi(player, UiRoot);
        }

        /// <summary>
        /// Апгрейд блока до выбранного типа и скина.
        /// Инварианты: блок не меняется при недостатке ресурсов, отсутствии права строительства,
        /// невыкупленном DLC-скине или отсутствии пары (тип, скин) в данных префаба.
        /// Право rremove.nocost снимает затраты полностью. Сложность: O(1).
        /// Постусловие: при успехе таймер режима сбрасывается на полную длительность.
        /// </summary>
        private void UpgradeBlock(BasePlayer player, BuildingBlock block, UpgradeState state)
        {
            // CHANGE: Право апгрейда (rremove.upgrade) перепроверяется на каждом действии:
            // CHANGE: снятие привилегии немедленно выключает режим.
            if (!permission.UserHasPermission(player.UserIDString, PermUpgrade))
            {
                Toast(player, Msg(player, "NoPermission"));
                DeactivateUpgrade(player.userID);
                return;
            }

            var grade = state.Grade;
            if (block == null || block.IsDestroyed)
                return;
            // CHANGE: Доступ к скину мог пропасть (снята привилегия FreeSkinPermission): выбор
            // CHANGE: сбрасывается на «Без скина», апгрейд продолжается стандартным скином,
            // CHANGE: а не блокируется ошибкой (недоступный скин по-прежнему не применяется).
            if (state.Skin != 0UL && !CanUseBuildingSkin(player, state.Skin, IsAlwaysUnlocked(grade, state.Skin)))
            {
                SetSelectedSkin(player, grade, 0UL);
                state.Skin = 0UL;
                Toast(player, Msg(player, "SkinFallback"));
            }
            // CHANGE: No-op учитывает скин и цвет: полное совпадение (марка + скин + цвет)
            // CHANGE: — единственный no-op; перескин и перекраска той же марки — легитимные
            // CHANGE: действия (ваниль считает их дешевле — 20% стоимости, см.
            // CHANGE: ConstructionGrade.UpdateCosts), случайный цвет (0) перекрашивает блок.
            var colourNoop = state.Skin == 0UL
                || !colourPalettes.ContainsKey(state.Skin)
                || state.Colour == 0u
                || block.customColour == state.Colour;
            if (block.grade == grade && block.skinID == state.Skin && colourNoop)
                return;
            if (!player.CanBuild())
            {
                Toast(player, Msg(player, "UpgradeNoBuildAuth"));
                return;
            }

            // CHANGE: Требование шкафа (конфиг): авторизация в строительном шкафу зоны апгрейда.
            // CHANGE: GetBuildingPrivilege покрывает все варианты шкафа — ванильный, Shockbyte, Retro.
            if (configData.Upgrade.UpgradeRequireCupboard)
            {
                var privilege = player.GetBuildingPrivilege(player.WorldSpaceBounds());
                if (privilege == null || !privilege.IsAuthed(player))
                {
                    Toast(player, Msg(player, "UpgradeNoCupboard"));
                    return;
                }
            }

            // CHANGE: Скин постройки: ищется точная пара (тип, скин) в данных префаба блока.
            // CHANGE: Construction.GetGrade при отсутствии пары молча возвращает defaultGrade,
            // CHANGE: поэтому совпадение проверяется по gradeBase; неподдержанный скин откатывается
            // CHANGE: на стандартный (актуально для твиней при стройке планом с включенным скином).
            var skinGrade = ResolveSkinGrade(block, grade, state.Skin, out var fallbackUsed);
            if (skinGrade == null)
                return;

            // CHANGE: Владение DLC-скином проверяется серверно — зеркало ванильной проверки
            // CHANGE: в DoUpgradeToGrade (steamInventory.HasItem по BuildingGrade.skin), иначе
            // CHANGE: скины раздавались бы бесплатно всем. Обход — только привилегия из конфига.
            if (!CanUseBuildingSkin(player, skinGrade.gradeBase.skin, skinGrade.gradeBase.alwaysUnlock))
            {
                Toast(player, Msg(player, "SkinNotOwned"));
                return;
            }
            if (fallbackUsed)
                Toast(player, Msg(player, "SkinFallback"));

            var noCost = permission.UserHasPermission(player.UserIDString, PermNoCost);
            if (!noCost)
            {
                // CHANGE: Стоимость считается по паре (тип, скин): перескин той же марки дешевле.
                if (!block.CanAffordUpgrade(grade, skinGrade.gradeBase.skin, player))
                {
                    Toast(player, Msg(player, "UpgradeNoResources"));
                    return;
                }
                block.PayForUpgrade(skinGrade, player);
            }
            // CHANGE: ChangeGradeAndSkin сам восстанавливает прочность (SetHealthToMax внутри ChangeGrade).
            // CHANGE: 1.6.0 — цвет палитры передаётся в ванильный механизм: ChangeGradeAndSkin
            // CHANGE прогоняет его через GetStartingDetailColour (0 — случайный цвет палитры).
            // CHANGE: Цвет передаётся ТОЛЬКО для скинов с палитрой: у прочих скинов
            // CHANGE: currentSkin == null и вызов GetStartingDetailColour падал бы NRE
            // CHANGE: (Assembly-CSharp_chunk5:14802 — color.HasValue ? currentSkin.…).
            var paletteColour = state.Skin != 0UL && colourPalettes.ContainsKey(state.Skin)
                ? state.Colour
                : (uint?)null;
            block.ChangeGradeAndSkin(
                skinGrade.gradeBase.type,
                skinGrade.gradeBase.skin,
                playEffect: true,
                updateSkin: true,
                color: paletteColour);

            // CHANGE: Успешный апгрейд продлевает режим: таймер сбрасывается и сразу перерисовывается.
            state.ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(1f, configData.Upgrade.UpgradeModeDuration);
            UpdateUpgradeLabel(player, state, Mathf.CeilToInt(state.ExpiresAt - Time.realtimeSinceStartup));
        }

        /// <summary>
        /// Отправляет игроку нативное радиальное меню (RPC OpenPie) с тремя режимами и пунктом закрытия.
        /// Инвариант: menuAdmin/menuAll помечены disabled для игроков без rremove.admin — выбор блокируется на клиенте.
        /// Сложность: O(1).
        /// </summary>
        private void SendRadialMenu(BasePlayer player)
        {
            // CHANGE: Открытие меню сбрасывает выборы скинов, потерявшие доступ (снята привилегия):
            // CHANGE: строка «Скин: …» и подсветка не могут показать недоступный скин.
            PruneUnavailableSkinSelections(player);
            // CHANGE: 1.6.0 — новое главное меню отменяет незавершённый выбор цвета: игрок
            // CHANGE: закрыл цветовое колесо без выбора (ESC) и начал выбор заново.
            pendingColourSelections.Remove(player.userID);
            var pie = Pool.Get<ProtoBuf.CustomPie>();
            try
            {
                pie.menus = Pool.Get<List<ProtoBuf.CustomPieMenu>>();
                var pieConfig = configData.Gui.Pie;
                var admin = IsAdmin(player);
                AddPieOption(
                    pie, Msg(player, "PieDefault"), Msg(player, "PieDefaultDesc"),
                    "rremove.mode.default", pieConfig.DefaultIcon, disabled: false);
                AddPieOption(
                    pie, Msg(player, "PieAdmin"), Msg(player, "PieAdminDesc"),
                    "rremove.mode.admin", pieConfig.AdminIcon, disabled: !admin);
                AddPieOption(
                    pie, Msg(player, "PieAll"), Msg(player, "PieAllDesc"),
                    "rremove.mode.all", pieConfig.AllIcon, disabled: !admin);

                // CHANGE: Типы автоапгрейда — только при включенной функции; пункты видны всем,
                // CHANGE: но заблокированы без привилегии rremove.upgrade (rremove.admin их не даёт).
                if (configData.Upgrade.EnableAutoUpgrade)
                {
                    var canUpgrade = permission.UserHasPermission(player.UserIDString, PermUpgrade);
                    // CHANGE: Пейджер Q/E и строка скина видны только игроку, которому есть что
                    // листать: купленный DLC-скин (проверка Steam-инвентаря) или привилегия
                    // FreeSkinPermission. Без DLC и привилегии пункты апгрейда снова чистые.
                    var woodPager = ShowInlinePager(player, BuildingGrade.Enum.Wood);
                    AddPieOption(
                        pie, Msg(player, "PieWood"), GradeOptionDescription(player, BuildingGrade.Enum.Wood, woodPager),
                        "rremove.grade.wood", pieConfig.WoodIcon, disabled: !canUpgrade,
                        next: woodPager ? SkinPageCommand(BuildingGrade.Enum.Wood, 1) : string.Empty,
                        prev: woodPager ? SkinPageCommand(BuildingGrade.Enum.Wood, -1) : string.Empty);
                    var stonePager = ShowInlinePager(player, BuildingGrade.Enum.Stone);
                    AddPieOption(
                        pie, Msg(player, "PieStone"), GradeOptionDescription(player, BuildingGrade.Enum.Stone, stonePager),
                        "rremove.grade.stone", pieConfig.StoneIcon, disabled: !canUpgrade,
                        next: stonePager ? SkinPageCommand(BuildingGrade.Enum.Stone, 1) : string.Empty,
                        prev: stonePager ? SkinPageCommand(BuildingGrade.Enum.Stone, -1) : string.Empty);
                    var metalPager = ShowInlinePager(player, BuildingGrade.Enum.Metal);
                    AddPieOption(
                        pie, Msg(player, "PieMetal"), GradeOptionDescription(player, BuildingGrade.Enum.Metal, metalPager),
                        "rremove.grade.metal", pieConfig.MetalIcon, disabled: !canUpgrade,
                        next: metalPager ? SkinPageCommand(BuildingGrade.Enum.Metal, 1) : string.Empty,
                        prev: metalPager ? SkinPageCommand(BuildingGrade.Enum.Metal, -1) : string.Empty);
                    var hqmPager = ShowInlinePager(player, BuildingGrade.Enum.TopTier);
                    AddPieOption(
                        pie, Msg(player, "PieHqm"), GradeOptionDescription(player, BuildingGrade.Enum.TopTier, hqmPager),
                        "rremove.grade.hqm", pieConfig.HqmIcon, disabled: !canUpgrade,
                        next: hqmPager ? SkinPageCommand(BuildingGrade.Enum.TopTier, 1) : string.Empty,
                        prev: hqmPager ? SkinPageCommand(BuildingGrade.Enum.TopTier, -1) : string.Empty);

                    // CHANGE: 1.5.3 — кнопка «Скины построек» и её подменю удалены по требованию
                    // CHANGE: владельца: выбор скина — только пейджер Q/E у типов апгрейда.
                }

                AddPieOption(
                    pie, Msg(player, "PieClose"), Msg(player, "PieCloseDesc"),
                    string.Empty, pieConfig.CloseIcon, disabled: false);
                CommunityEntity.ServerInstance.SendPie(player, pie);
            }
            finally
            {
                // CHANGE: паттерн Facepunch (pietest): CustomPie пулится и освобождается после отправки.
                pie.Dispose();
            }
        }

        /// <summary>
        /// Добавляет пункт в CustomPie. Постусловие: command — консольная команда плагина или "" (закрыть меню);
        /// next/prev — команды ванильного пейджера (клиент рисует кнопки Q/E, Rust.Data_chunk3:21654);
        /// color != default — пункт рисуется цветным кружком (1.6.0, колесо баллончика).
        /// </summary>
        private void AddPieOption(
            ProtoBuf.CustomPie pie, string name, string description, string command, string icon, bool disabled,
            bool selected = false, string next = "", string prev = "", Color color = default)
        {
            var option = Pool.Get<ProtoBuf.CustomPieMenu>();
            option.name = name;
            option.description = description;
            option.command = command;
            option.sprite = icon;
            option.disabled = disabled;
            // CHANGE: selected подсвечивает клиенту текущий выбор (скин постройки) в подменю.
            option.selected = selected;
            // CHANGE: ванильный пейджер Q/E — команды листания (паттерн pietest, Assembly-CSharp_chunk5:28820).
            option.nextCommand = next;
            option.prevCommand = prev;
            // CHANGE: 1.6.0 — цвет пункта (цветные кружки палитры контейнеров).
            // CHANGE: 1.6.4 — colorMode жёстко CustomColor (0): клиент окрашивает иконку
            // CHANGE: цветом из option.color; SpriteColor (1) из конфига удалён. Для пунктов
            // CHANGE: без цвета оба поля в дефолте — клиент рисует иконку как обычно.
            option.color = color;
            option.colorMode = 0;
            pie.menus.Add(option);
        }

        /// <summary>Включает режим удаления на время из конфигурации и показывает GUI-панель режима.</summary>
        private void Activate(BasePlayer player, RemoveMode mode)
        {
            // CHANGE: Первое использование режима помечает игрока «знающим» — подсказка о бинде
            // больше не показывается (клиентский бинд серверно неопределим, использование — детектируемый факт).
            if (knownUsers.Add(player.userID))
                Interface.Oxide.DataFileSystem.WriteObject(DataFile, knownUsers);
            var duration = Mathf.Max(1f, configData.Remove.ModeDuration);
            activeRemovers[player.userID] = new RemoveState
            {
                Mode = mode,
                ExpiresAt = Time.realtimeSinceStartup + duration,
            };
            // CHANGE: Информация о режиме (тип и остаток времени) отображается в панели, а не в чате.
            // CHANGE: Остаток времени передаётся в панель сразу — раньше лейбл таймера
            // CHANGE: создавался пустым и заполнялся только первым тиком секундного таймера.
            var secondsLeft = Mathf.CeilToInt(duration);
            ShowRemoveUi(player, mode, secondsLeft);
        }

        /// <summary>Выключает режим удаления игрока: гасит GUI и снимает состояние.</summary>
        private void Deactivate(ulong playerId, bool destroyUi = true)
        {
            if (!activeRemovers.Remove(playerId))
                return;
            if (destroyUi && BasePlayer.FindByID(playerId) is { } player)
                CuiHelper.DestroyUi(player, UiRoot);
        }

        // CHANGE: Админские функции доступны только по праву rremove.admin — доступ по уровню
        // CHANGE: администратора (AuthLevel) убран по требованию.
        /// <summary>Проверяет право rremove.admin. AuthLevel больше не даёт админских функций плагина.</summary>
        private bool IsAdmin(BasePlayer player) =>
            permission.UserHasPermission(player.UserIDString, PermAdmin);

        #endregion Чат-команды

        // CHANGE: Скины построек (DLC): выбор скина пейджером Q/E на каждый тип апгрейда,
        // CHANGE: реестр игровых скинов и серверные проверки владения DLC.
        #region Скины построек

        /// <summary>Ключ типа апгрейда в командах/конфиге. Постусловие: «wood|stone|metal|hqm».</summary>
        private static string GradeKey(BuildingGrade.Enum grade) =>
            grade switch
            {
                BuildingGrade.Enum.Wood => "wood",
                BuildingGrade.Enum.Stone => "stone",
                BuildingGrade.Enum.Metal => "metal",
                _ => "hqm",
            };

        /// <summary>Ключ локализации названия типа (GradeWood/GradeStone/GradeMetal/GradeHqm).</summary>
        private static string GradeLangKey(BuildingGrade.Enum grade) =>
            grade switch
            {
                BuildingGrade.Enum.Wood => "GradeWood",
                BuildingGrade.Enum.Stone => "GradeStone",
                BuildingGrade.Enum.Metal => "GradeMetal",
                _ => "GradeHqm",
            };

        /// <summary>Список скинов из конфига для типа апгрейда. Инвариант: результат не null.</summary>
        private List<SkinEntry> GetSkinEntries(BuildingGrade.Enum grade) =>
            grade switch
            {
                BuildingGrade.Enum.Wood => configData.Skins.WoodSkins,
                BuildingGrade.Enum.Stone => configData.Skins.StoneSkins,
                BuildingGrade.Enum.Metal => configData.Skins.MetalSkins,
                _ => configData.Skins.HqmSkins,
            };

        /// <summary>Списки конфига, сопоставленные типам апгрейда (автозаполнение и валидация).</summary>
        private IEnumerable<(BuildingGrade.Enum Grade, List<SkinEntry> Entries)> SkinConfigLists()
        {
            yield return (BuildingGrade.Enum.Wood, configData.Skins.WoodSkins);
            yield return (BuildingGrade.Enum.Stone, configData.Skins.StoneSkins);
            yield return (BuildingGrade.Enum.Metal, configData.Skins.MetalSkins);
            yield return (BuildingGrade.Enum.TopTier, configData.Skins.HqmSkins);
        }

        /// <summary>Выбранный игроком скин типа. Постусловие: 0 — стандартный скин.</summary>
        private ulong GetSelectedSkin(ulong userId, BuildingGrade.Enum grade) =>
            selectedSkins.TryGetValue(userId, out var prefs) && prefs.TryGetValue(grade, out var skin)
                ? skin
                : 0UL;

        /// <summary>
        /// Запоминает выбранный скин типа и мгновенно применяет его к активному режиму апгрейда
        /// этого типа (без сброса таймера и перерисовки GUI).
        /// Сложность: O(1) времени и памяти.
        /// </summary>
        private void SetSelectedSkin(BasePlayer player, BuildingGrade.Enum grade, ulong skinId)
        {
            if (!selectedSkins.TryGetValue(player.userID, out var prefs))
                selectedSkins[player.userID] = prefs = new Dictionary<BuildingGrade.Enum, ulong>();
            prefs[grade] = skinId;
            if (upgradeGrades.TryGetValue(player.userID, out var active) && active.Grade == grade)
                active.Skin = skinId;
        }

        /// <summary>
        /// Сбрасывает выбор скинов, к которым игрок потерял доступ (снята привилегия
        /// FreeSkinPermission / нет владения DLC): выбор каждого типа возвращается к
        /// «Без скина», активный режим апгрейда синхронизируется через SetSelectedSkin.
        /// Инвариант: после вызова любой выбранный скин проходит CanUseBuildingSkin.
        /// Сложность: O(G·g), G — типы апгрейда, g — записи скинов типа в конфиге.
        /// </summary>
        private void PruneUnavailableSkinSelections(BasePlayer player)
        {
            foreach (var (grade, _) in SkinConfigLists())
            {
                var skinId = GetSelectedSkin(player.userID, grade);
                if (skinId == 0UL)
                    continue;
                if (CanUseBuildingSkin(player, skinId, IsAlwaysUnlocked(grade, skinId)))
                    continue;
                SetSelectedSkin(player, grade, 0UL);
            }
        }

        /// <summary>Название выбранного скина типа (для тостов и описаний пунктов меню).</summary>
        private string SelectedSkinName(BasePlayer player, BuildingGrade.Enum grade) =>
            SkinDisplayName(player, GetSelectedSkin(player.userID, grade));

        /// <summary>Ключ локализации имени скина: Skin{ID скина}, например Skin2959817484.</summary>
        private static string SkinNameLangKey(ulong skinId) => $"Skin{skinId}";

        /// <summary>
        /// Регистрирует ключи локализации имён скинов (Skin{ID скина}) из игровых данных:
        /// значением служит каноническое английское имя (как в ванильном меню апгрейда),
        /// одинаковое для en и ru. Lang мержит словарь с существующим файлом: новые ключи
        /// добавляются, правки сервера (переводы в lang/&lt;язык&gt;/RRemove.json) не
        /// перезатираются между рестартами.
        /// Предусловие: BuildSkinRegistry уже выполнен (skinNamesById заполнен).
        /// Постусловие: у каждого скина реестра есть ключ Skin{ID}; имя для ID 0 — ключ PieSkinNone.
        /// Сложность: O(s) времени, O(s) памяти; s — число скинов в реестре.
        /// </summary>
        private void RegisterSkinNameMessages()
        {
            // CHANGE: КЛЮЧЕВАЯ ПРАВКА: Oxide Lang.RegisterMessages ЗАМЕНЯЕТ весь словарь языка плагина,
            // поэтому повторная регистрация только имён скинов стирала все переводы из LoadDefaultMessages
            // (в GUI сыпались сырые ключи: "TimeLeft", "GuiUpgradeTitle", "PieWood" и т.д.).
            // Порядок мержа: зарегистрированные кодом переводы → правки сервера из файла
            // lang/<язык>/RRemove.json (приоритет сервера) → канонические имена скинов из игровых
            // данных ТОЛЬКО для отсутствующих ключей (серверные переводы скинов не перезатираются).
            var namesEn = new Dictionary<string, string>(
                lang.GetMessages("en", this) ?? new Dictionary<string, string>()
            );
            var namesRu = new Dictionary<string, string>(
                lang.GetMessages("ru", this) ?? new Dictionary<string, string>()
            );
            MergeLangFileOverlay(namesEn, "en");
            MergeLangFileOverlay(namesRu, "ru");

            foreach (var pair in skinNamesById)
            {
                string key = SkinNameLangKey(pair.Key);
                if (!namesEn.ContainsKey(key))
                    namesEn[key] = pair.Value;
                if (!namesRu.ContainsKey(key))
                    namesRu[key] = pair.Value;
            }

            // CHANGE: Скины из конфига, которых нет в игровых данных (например, ID из другого
            // источника), тоже получают ключи локализации — иначе в GUI показывался сырой "Skin{ID}".
            foreach (var (_, entries) in SkinConfigLists())
            {
                foreach (var entry in entries)
                {
                    if (entry?.SkinId == null || entry.SkinId == 0UL)
                        continue;
                    string key = SkinNameLangKey(entry.SkinId);
                    if (namesEn.ContainsKey(key))
                        continue;
                    string fallback = LookupItemSkinName(entry.SkinId) ?? $"Skin {entry.SkinId}";
                    namesEn[key] = fallback;
                    namesRu[key] = fallback;
                }
            }

            lang.RegisterMessages(namesEn, this, "en");
            lang.RegisterMessages(namesRu, this, "ru");
        }

        /// <summary>
        /// Накладывает поверх словаря переводов правки сервера из файла lang/&lt;язык&gt;/RRemove.json:
        /// lang.GetMessages возвращает только словари, зарегистрированные кодом, поэтому ключи,
        /// добавленные сервером напрямую в файл (например, Skin10220 для скина из конфига вне
        /// игровых данных), иначе терялись бы при перерегистрации.
        /// Постусловие: entries содержит все пары файла; значения файла приоритетнее кодовых.
        /// Сложность: O(n), n — число ключей файла.
        /// </summary>
        private void MergeLangFileOverlay(Dictionary<string, string> entries, string langCode)
        {
            try
            {
                var fromDisk =
                    Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, string>>(
                        $"lang/{langCode}/{Title}"
                    ) ?? new Dictionary<string, string>();
                foreach (var pair in fromDisk)
                {
                    if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                        entries[pair.Key] = pair.Value;
                }
            }
            catch (Exception ex)
            {
                // Файл может отсутствовать или быть повреждён — не критично.
                Puts($"[RRemove] Не удалось прочитать lang/{langCode}/{Title}.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Отображаемое имя скина: 0 — «Без скина» (PieSkinNone); иначе ключ локализации
        /// Skin{ID скина}; при отсутствии ключа (скин добавлен в конфиг вручную вне игровых
        /// данных) — английское имя из реестра, иначе «ID {id}».
        /// Инвариант: результат непустой. Сложность: O(1) времени и памяти.
        /// </summary>
        private string SkinDisplayName(BasePlayer player, ulong skinId)
        {
            if (skinId == 0UL)
                return Msg(player, "PieSkinNone");
            var localized = lang.GetMessage(SkinNameLangKey(skinId), this, player.UserIDString);
            // CHANGE: Oxide GetMessage возвращает САМ КЛЮЧ, если перевод не зарегистрирован —
            // такое значение нельзя показывать ("Скин: Skin10220"). Откат: имя скина из
            // assets/skins.asset (ItemSkinDirectory), затем имя из реестра, затем ID.
            if (!string.IsNullOrEmpty(localized) && localized != SkinNameLangKey(skinId))
                return localized;
            var directoryName = LookupItemSkinName(skinId);
            if (!string.IsNullOrEmpty(directoryName))
                return directoryName;
            return skinNamesById.TryGetValue(skinId, out var name) ? name : $"ID {skinId}";
        }

        /// <summary>
        /// Ищет каноническое имя скина в игровых данных assets/skins.asset (ItemSkinDirectory).
        /// Постусловие: null — скин с таким ID в директории не найден.
        /// Сложность: O(s), s — число скинов в директории (вызывается только при отсутствии ключа локализации).
        /// </summary>
        private static string? LookupItemSkinName(ulong skinId)
        {
            try
            {
                var skins = ItemSkinDirectory.Instance?.skins;
                if (skins == null)
                    return null;
                foreach (var skin in skins)
                {
                    if ((ulong)skin.id != skinId)
                        continue;

                    // CHANGE: skin.name — это ПУТЬ АССЕТА ("assets/prefabs/misc/buildingskins/
                    // space_station/space_station.sitem.asset"), а не название. Пытаемся взять
                    // displayName предмета скина (skin.itemid), иначе «очеловечиваем» путь:
                    // последний сегмент без ".sitem.asset", подчёркивания → пробелы.
                    var skinItemDef = ItemManager.FindItemDefinition(skin.itemid);
                    string display = skinItemDef?.displayName?.english;
                    if (string.IsNullOrEmpty(display))
                        display = PrettifySkinAssetPath(skin.name);
                    if (!string.IsNullOrEmpty(display))
                        return display;
                }
            }
            catch (Exception)
            {
                // assets/skins.asset недоступен — не критично, сработает следующий fallback.
            }
            return null;
        }

        /// <summary>
        /// «очеловечивает» путь ассета скина: "assets/prefabs/misc/buildingskins/space_station/
        /// space_station.sitem.asset" → "Space Station".
        /// Постусловие: null — путь пустой или не заканчивается на .sitem.asset.
        /// Сложность: O(len(path)).
        /// </summary>
        private static string? PrettifySkinAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            const string suffix = ".sitem.asset";
            int slash = path.LastIndexOf('/');
            string segment =
                slash >= 0 ? path.Substring(slash + 1) : path;
            if (segment.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                segment = segment.Substring(0, segment.Length - suffix.Length);
            if (string.IsNullOrEmpty(segment))
                return null;
            segment = segment.Replace('_', ' ');
            return char.ToUpperInvariant(segment[0]) + segment.Substring(1);
        }

        /// <summary>
        /// Ищет в данных префаба блока точную пару (тип, скин).
        /// Инвариант: Construction.GetGrade при отсутствии пары молча возвращает defaultGrade,
        /// поэтому совпадение проверяется по gradeBase.type/gradeBase.skin.
        /// Постусловие: null — пары нет (при skin != 0 пробуется откат на стандартный скин,
        /// fallbackUsed сигнализирует об откате). Сложность: O(g), g — число грейдов префаба.
        /// </summary>
        private static ConstructionGrade? ResolveSkinGrade(
            BuildingBlock block, BuildingGrade.Enum grade, ulong skin, out bool fallbackUsed)
        {
            fallbackUsed = false;
            var definition = block.blockDefinition;
            if (definition == null)
                return null;

            var found = definition.GetGrade(grade, skin);
            if (found?.gradeBase != null && found.gradeBase.type == grade && found.gradeBase.skin == skin)
                return found;
            if (skin == 0UL)
                return null;

            var fallback = definition.GetGrade(grade, 0UL);
            if (fallback?.gradeBase != null && fallback.gradeBase.type == grade && fallback.gradeBase.skin == 0UL)
            {
                fallbackUsed = true;
                return fallback;
            }
            return null;
        }

        /// <summary>
        /// Право на использование скина постройки — зеркало ванильной проверки DoUpgradeToGrade
        /// (Assembly-CSharp_chunk5:14757): alwaysUnlock или владение Steam-предметом скина
        /// (player.blueprints.steamInventory.HasItem — инвентарь клиента верифицируется сервером
        /// подписью Steam при подключении, подделать нельзя).
        /// Инвариант: бесплатно скин не выдаётся никому — единственный обход проверки
        /// (для администраторов) это привилегия FreeSkinPermission из конфига.
        /// Предусловие: skinId — BuildingGrade.skin (Steam item definition ID).
        /// Сложность: O(k), k — число предметов Steam-инвентаря игрока.
        /// </summary>
        private bool CanUseBuildingSkin(BasePlayer player, ulong skinId, bool alwaysUnlock)
        {
            if (skinId == 0UL || alwaysUnlock)
                return true;
            // CHANGE: Steam item definition ID умещается в int (ваниль кастует так же);
            // CHANGE: некорректный ID из конфига не проходит проверку (fail-closed).
            if (skinId > int.MaxValue)
                return false;
            if (permission.UserHasPermission(player.UserIDString, configData.Skins.FreeSkinPermission))
                return true;
            var inventory = player.blueprints?.steamInventory;
            return inventory != null && inventory.HasItem((int)skinId);
        }

        /// <summary>
        /// Строит реестр игровых DLC-скинов построек из ванильного источника списка —
        /// PrefabAttribute.server.FindAll&lt;ConstructionGrade&gt;(SkinListPrefabId), тот же запрос,
        /// что и в Admin.FindBuildingSkin. Скины (skin != 0) группируются по типу апгрейда;
        /// параллельно наполняется карта имён skinNamesById (источник ключей локализации и
        /// fallback имён). Сложность: O(g), g — число ConstructionGrade префаба-источника.
        /// </summary>
        private void BuildSkinRegistry()
        {
            gameSkinRegistry.Clear();
            skinNamesById.Clear();
            foreach (var constructionGrade in PrefabAttribute.server.FindAll<ConstructionGrade>(SkinListPrefabId))
            {
                var gradeBase = constructionGrade?.gradeBase;
                // CHANGE: 1.5.0 — фильтр enabledInStandalone убран: флаг существует только для
                // CHANGE: админской консольной команды (Admin.ApplySkinInternal, chunk25:17707),
                // CHANGE: а ванильная проверка игрока в DoUpgradeToGrade (chunk5:14757) его не
                // CHANGE: читает. Сезонные скины (пряничный — только на Рождество, отдельным DLC
                // CHANGE: не продаётся) теперь видны в меню круглый год; доступ по-прежнему
                // CHANGE: решает проверка владения Steam-предметом / alwaysUnlock / FreeSkinPermission.
                if (gradeBase == null || gradeBase.skin == 0UL)
                    continue;
                if (!gameSkinRegistry.TryGetValue(gradeBase.type, out var list))
                    gameSkinRegistry[gradeBase.type] = list = new List<SkinInfo>();
                if (list.Any(info => info.SkinId == gradeBase.skin))
                    continue;
                // CHANGE: Имя нормализуется сразу (пустая строка из данных игры → «Skin {ID}»):
                // CHANGE: одно значение уходит и в реестр, и в ключи локализации без повторных проверок.
                var englishName = gradeBase.upgradeMenu.name?.english;
                if (string.IsNullOrEmpty(englishName))
                    englishName = $"Skin {gradeBase.skin}";
                list.Add(new SkinInfo
                {
                    SkinId = gradeBase.skin,
                    AlwaysUnlock = gradeBase.alwaysUnlock,
                    EnglishName = englishName,
                    SupportsColourChange = gradeBase.supportsColourChange,
                });
                skinNamesById[gradeBase.skin] = englishName;
            }
            if (gameSkinRegistry.Count == 0)
                PrintWarning("В игровых данных не найдено ни одного скина построек — проверьте обновление сервера.");
        }

        /// <summary>
        /// Строит реестр палитр скинов с поддержкой цвета (колесо баллончика, 1.6.0):
        /// перебирает ConstructionGrade канонического списка скинов (SkinListPrefabId — тот же
        /// источник, что у BuildSkinRegistry), для каждого со скином (skin != 0) и ванильным
        /// флагом supportsColourChange достаёт палитру ConstructionSkin_CustomDetail.
        /// ColourLookup.AllColours из префаба варианта скина (ConstructionGrade.skinObject).
        /// Палитра живет на варианте скина — одна на скин, поэтому ключом служит Steam item
        /// definition ID скина.
        /// Предусловие: вызывается после загрузки игровых данных (OnServerInitialized).
        /// Постусловие: colourPalettes содержит только скины с непустой палитрой.
        /// Сложность: O(g), g — грейды списка скинов; выполняется один раз при старте.
        /// </summary>
        private void BuildColourPaletteRegistry()
        {
            colourPalettes.Clear();
            // CHANGE: Источник — тот же префаб-список скинов, что и у BuildSkinRegistry:
            // CHANGE: FindAll<T> требует prefabID (CS7036), глобального перебора атрибутов
            // CHANGE: API PrefabAttribute не даёт; SkinListPrefabId — канонический список.
            foreach (var constructionGrade in PrefabAttribute.server.FindAll<ConstructionGrade>(SkinListPrefabId))
            {
                if (constructionGrade == null)
                    continue;
                var gradeBase = constructionGrade.gradeBase;
                if (
                    gradeBase == null
                    || gradeBase.skin == 0UL
                    || !gradeBase.supportsColourChange
                    || colourPalettes.ContainsKey(gradeBase.skin)
                )
                    continue;
                var skinObject = constructionGrade.skinObject;
                if (skinObject == null || !skinObject.isValid)
                    continue;

                // CHANGE: Prefab варианта скина доступен и на сервере: именно через
                // skinObject.Get() сервер инстанцирует скин в ChangeSkin (chunk5:14953).
                var detail = skinObject.Get()
                    ?.GetComponentInChildren<ConstructionSkin_CustomDetail>(true);
                var colours = detail?.ColourLookup?.AllColours;
                if (colours == null || colours.Length == 0)
                    continue;
                colourPalettes[gradeBase.skin] = colours;
            }
            if (colourPalettes.Count == 0)
                PrintWarning("Скины с палитрой цветов (контейнеры) в игровых данных не найдены.");
        }

        /// <summary>
        /// Ванильный флаг alwaysUnlock скина (владение не требуется).
        /// Постусловия: скин 0 — всегда true; скин вне реестра — false (fail-closed:
        /// CanUseBuildingSkin потребует владение Steam-предметом).
        /// Сложность: O(g) по размеру реестра типа.
        /// </summary>
        private bool IsAlwaysUnlocked(BuildingGrade.Enum grade, ulong skinId)
        {
            if (skinId == 0UL)
                return true;
            return gameSkinRegistry.TryGetValue(grade, out var list)
                && list.Any(info => info.SkinId == skinId && info.AlwaysUnlock);
        }

        /// <summary>
        /// Синхронизирует списки скинов конфига с реестром игры: при включенном AutoFillSkins
        /// добавляет отсутствующие DLC-скины (в конфиг пишется только ID — имя берётся из
        /// локализации), о несуществующих ID из конфига предупреждает. Конфиг сохраняется
        /// только при изменениях. Сложность: O(C·G), C — записей конфига, G — скинов в реестре.
        /// </summary>
        private void SyncSkinConfig()
        {
            var changed = false;
            foreach (var (grade, entries) in SkinConfigLists())
            {
                if (configData.Skins.AutoFillSkins && gameSkinRegistry.TryGetValue(grade, out var discovered))
                {
                    foreach (var info in discovered)
                    {
                        if (entries.Any(entry => entry.SkinId == info.SkinId))
                            continue;
                        // CHANGE: В конфиг пишется только ID скина — имя живёт в локализации
                        // CHANGE: (ключ Skin{ID}, зарегистрированный RegisterSkinNameMessages).
                        entries.Add(new SkinEntry { SkinId = info.SkinId });
                        changed = true;
                    }
                }

                foreach (var entry in entries)
                {
                    if (entry.SkinId == 0UL)
                        continue;
                    var known = gameSkinRegistry.TryGetValue(grade, out var list)
                        && list.Any(info => info.SkinId == entry.SkinId);
                    if (!known)
                        PrintWarning(
                            $"Скин (ID {entry.SkinId}) не найден в игровых данных типа «{GradeKey(grade)}». " +
                            "Проверьте ID командой rremove.skins.");
                }
            }
            if (changed)
                SaveConfig();
        }

        /// <summary>
        /// rremove.skins — печатает реестр найденных в игре скинов построек по типам апгрейда
        /// (Steam ID скина, alwaysUnlock, наличие в конфиге) — для заполнения конфига.
        /// </summary>
        [ConsoleCommand("rremove.skins")]
        private void ConsoleCmdSkinList(ConsoleSystem.Arg arg)
        {
            var report = new StringBuilder();
            foreach (var (grade, entries) in SkinConfigLists())
            {
                report.AppendLine($"[{GradeKey(grade)}]");
                if (!gameSkinRegistry.TryGetValue(grade, out var discovered) || discovered.Count == 0)
                {
                    report.AppendLine("  скины не найдены в игровых данных");
                    continue;
                }
                foreach (var info in discovered)
                {
                    var inConfig = entries.Any(entry => entry.SkinId == info.SkinId)
                        ? "в конфиге"
                        : "НЕТ в конфиге";
                    report.AppendLine($"  {info.SkinId} — {info.EnglishName} (alwaysUnlock: {info.AlwaysUnlock}; {inConfig})");
                }
            }
            arg.ReplyWith(report.ToString());
        }

        /// <summary>Ключ локализации базового описания типа апгрейда (PieWoodDesc/…/PieHqmDesc).</summary>
        private static string GradePieDescKey(BuildingGrade.Enum grade) =>
            grade switch
            {
                BuildingGrade.Enum.Wood => "PieWoodDesc",
                BuildingGrade.Enum.Stone => "PieStoneDesc",
                BuildingGrade.Enum.Metal => "PieMetalDesc",
                _ => "PieHqmDesc",
            };

        /// <summary>
        /// Описание пункта типа апгрейда: при включенном пейджере добавляет строку текущего скина —
        /// отображается в центре колеса под базовым описанием, как ванильный выбор скинов.
        /// </summary>
        private string GradeOptionDescription(BasePlayer player, BuildingGrade.Enum grade, bool skinsInline) =>
            skinsInline
                ? $"{Msg(player, GradePieDescKey(grade))}\n{string.Format(Msg(player, "PieSkinCurrent"), SelectedSkinName(player, grade))}"
                : Msg(player, GradePieDescKey(grade));

        /// <summary>Консольная команда пейджера типа апгрейда (direction: 1 — next/E, -1 — prev/Q).</summary>
        private static string SkinPageCommand(BuildingGrade.Enum grade, int direction) =>
            $"rremove.skinpage.{GradeKey(grade)} {(direction > 0 ? "next" : "prev")}";

        /// <summary>
        /// Список скинов типа, доступных игроку: всегда «Без скина» (0) + скины с подтверждённым
        /// владением DLC или привилегией FreeSkinPermission. Единый источник для листания Q/E
        /// и видимости пейджера в главном меню. Сложность: O(g), g — записи скинов типа в конфиге.
        /// </summary>
        private List<ulong> GetUsableSkins(BasePlayer player, BuildingGrade.Enum grade)
        {
            var usable = new List<ulong> { 0UL };
            foreach (var entry in GetSkinEntries(grade))
            {
                if (entry.SkinId == 0UL || usable.Contains(entry.SkinId))
                    continue;
                if (CanUseBuildingSkin(player, entry.SkinId, IsAlwaysUnlocked(grade, entry.SkinId)))
                    usable.Add(entry.SkinId);
            }
            return usable;
        }

        /// <summary>
        /// Видимость пейджера Q/E и строки скина у типа апгрейда: функции включены в конфиге
        /// и у игрока есть хотя бы один доступный скин (DLC/привилегия) — иначе пункт чистый.
        /// </summary>
        private bool ShowInlinePager(BasePlayer player, BuildingGrade.Enum grade) =>
            configData.Upgrade.EnableAutoUpgrade
            && configData.Skins.EnableBuildSkins
            && configData.Skins.EnableInlineSkinPaging
            && GetUsableSkins(player, grade).Count > 1;

        // CHANGE: Ванильский пейджер скинов Q/E у типов апгрейда: команда next/prev переключает
        // CHANGE: скин типа и переоткрывает меню (паттерн pietest_next, Assembly-CSharp_chunk5:28848 —
        // CHANGE: клиент заменяет открытый pie новым).
        [ConsoleCommand("rremove.skinpage.wood")]
        private void ConsoleCmdSkinPageWood(ConsoleSystem.Arg arg) => HandleSkinPage(arg, BuildingGrade.Enum.Wood);

        [ConsoleCommand("rremove.skinpage.stone")]
        private void ConsoleCmdSkinPageStone(ConsoleSystem.Arg arg) => HandleSkinPage(arg, BuildingGrade.Enum.Stone);

        [ConsoleCommand("rremove.skinpage.metal")]
        private void ConsoleCmdSkinPageMetal(ConsoleSystem.Arg arg) => HandleSkinPage(arg, BuildingGrade.Enum.Metal);

        [ConsoleCommand("rremove.skinpage.hqm")]
        private void ConsoleCmdSkinPageHqm(ConsoleSystem.Arg arg) => HandleSkinPage(arg, BuildingGrade.Enum.TopTier);

        /// <summary>
        /// Листает скин типа апгрейда (rremove.skinpage.&lt;тип&gt; next|prev) по кругу среди доступных
        /// игроку скинов и переоткрывает главное меню с обновлённым описанием.
        /// Инварианты: цикл всегда содержит «Без скина» (0) и проходит только по скинам, разрешённым
        /// проверкой владения (CanUseBuildingSkin); выбор немедленно виден активному режиму апгрейда
        /// (SetSelectedSkin синхронизирует UpgradeState.Skin).
        /// Предусловия: право rremove.upgrade, отсутствие рейд/комбат-блока, функции включены в конфиге.
        /// Постусловие: выбран скин на ±1 позицию с обходом по кругу; меню переотправлено.
        /// Вариантная функция: расстояние по кругу до целевого индекса убывает на каждом вызове (1 шаг).
        /// Сложность: O(g) времени, O(g) памяти; g — записи скинов типа в конфиге.
        /// </summary>
        private void HandleSkinPage(ConsoleSystem.Arg arg, BuildingGrade.Enum grade)
        {
            if (arg == null)
                return;
            var player = arg.Player();
            if (player == null || !player.IsConnected)
                return;
            if (!configData.Upgrade.EnableAutoUpgrade
                || !configData.Skins.EnableBuildSkins
                || !configData.Skins.EnableInlineSkinPaging)
                return;
            if (!permission.UserHasPermission(player.UserIDString, PermUpgrade))
            {
                Toast(player, Msg(player, "NoPermission"));
                return;
            }
            var deny = GetBlockDenyReason(player, out var denyLeft);
            if (deny != null)
            {
                DenyToast(player, deny, denyLeft);
                return;
            }

            // CHANGE: Листаются только доступные скины — единый источник с видимостью пейджера
            // CHANGE: (GetUsableSkins); одному «Без скина» листать нечего.
            var usable = GetUsableSkins(player, grade);
            if (usable.Count <= 1)
                return;

            var direction = arg.GetString(0).Equals("prev", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
            var index = usable.IndexOf(GetSelectedSkin(player.userID, grade));
            if (index < 0)
                index = 0;
            var target = usable[(index + direction + usable.Count) % usable.Count];
            // CHANGE: Клиент закрывает pie при исполнении любой его команды (в т.ч. nextCommand/
            // prevCommand) — держать колесо открытым при листании невозможно.
            // CHANGE: ReopenMenuOnSkinPage: true — мгновенное переоткрытие колеса с новым скином;
            // false — колесо НЕ переоткрывается (без мерцания/сброса курсора), выбранный скин
            // показывается GameTip-тостом.
            SetSelectedSkin(player, grade, target);
            if (configData.Skins.ReopenMenuOnSkinPage)
            {
                SendRadialMenu(player);
            }
            else
            {
                Toast(
                    player,
                    $"{Msg(player, GradeLangKey(grade))}: {SkinDisplayName(player, target)}"
                );
            }
        }

        #endregion Скины построек

        #region Удаление

        /// <summary>
        /// Хук удара молотком: активен только пока у игрока включен режим удаления.
        /// Инвариант: при любом обработанном ударе возвращается true — это отменяет ванильный
        /// BaseCombatEntity.DoRepair, который после entity.Kill() падает с NRE в BuildingBlock.MaxHealth().
        /// Сложность: O(1) для одиночного удаления, O(n) для /remove all (n — сущности строения).
        /// </summary>
        // CHANGE: Тип возврата object → object? — файл с #nullable enable, а хук легитимно
        // CHANGE: возвращает null (хук не активен); аннотация не влияет на рантайм-биндинг Oxide/Carbon.
        private object? OnHammerHit(BasePlayer player, HitInfo info)
        {
            if (player == null)
                return null;

            var hasRemoveMode = activeRemovers.TryGetValue(player.userID, out var state);
            var hasUpgradeMode = !hasRemoveMode
                && configData.Upgrade.EnableAutoUpgrade
                && upgradeGrades.ContainsKey(player.userID);
            if (!hasRemoveMode && !hasUpgradeMode)
                return null;

            var entity = info?.HitEntity;
            if (entity == null || entity.IsDestroyed)
                return null;

            // CHANGE: Удар по дочернему хранилищу карьера (fuelstorage/hopperoutput — отдельные
            // CHANGE: сущности со своим OwnerID) валидируем и удаляем по родительскому карьеру,
            // CHANGE: иначе ремув отдавал «чужой объект» вместо проверки/удаления самого карьера.
            if (entity is StorageContainer && entity.GetParentEntity() is MiningQuarry parentQuarry)
                entity = parentQuarry;

            // CHANGE: Мировые объекты (бочки, ящики с лутом на дорогах/в монументах) режим
            // CHANGE: молча игнорирует: ни проверки, ни сообщений — удар уходит в ваниль.
            // CHANGE: Тройная страховка: тип LootContainer, префиксы shortname, отсутствие владельца.
            if (
                entity is LootContainer
                || entity.OwnerID == 0u
                || entity.ShortPrefabName.Contains("barrel")
                || entity.ShortPrefabName.Contains("crate")
                || entity.ShortPrefabName.Contains("loot")
            )
            {
                if (entity is not DecayEntity)
                    return null;
            }

            // CHANGE: Режим апгрейда: удар киянкой поднимает блок до выбранного типа;
            // CHANGE: true гасит ванильный DoRepair, чтобы удар не лечил блок вместо апгрейда.
            if (!hasRemoveMode)
            {
                if (entity is BuildingBlock block)
                {
                    UpgradeBlock(player, block, upgradeGrades[player.userID]);
                    return true;
                }
                return null;
            }

            // CHANGE: В рейд/комбат-блоке ремув запрещён (конфиг) — удар уходит в ваниль.
            var deny = GetBlockDenyReason(player, out var denyLeft);
            if (deny != null)
            {
                DenyToast(player, deny, denyLeft);
                return null;
            }

            var adminBypass = state.Mode != RemoveMode.Default;
            var reason = ValidateRemove(player, entity, adminBypass);
            if (reason != null)
            {
                Toast(player, reason);
                return true;
            }

            // CHANGE: Внешние плагины могут запретить удаление, вернув строку из хука canRemove.
            if (Interface.CallHook("canRemove", player, entity) is string hookReason)
            {
                Toast(player, hookReason);
                return true;
            }

            if (state.Mode == RemoveMode.All)
            {
                RemoveAllConnected(player, entity);
                RefreshRemoveTimer(player, state);
                return true;
            }

            RefundEntity(player, entity);
            entity.Kill();
            // CHANGE: Чат-уведомление «Объект удалён» убрано — исчезновение объекта и есть обратная связь.
            // CHANGE: Успешный ремув продлевает режим: таймер сбрасывается и перерисовывается сразу,
            // CHANGE: как это сделано в режиме апгрейда.
            RefreshRemoveTimer(player, state);
            return true;
        }

        /// <summary>Сбрасывает таймер режима удаления на полную длительность и сразу обновляет лейбл.</summary>
        private void RefreshRemoveTimer(BasePlayer player, RemoveState state)
        {
            state.ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(1f, configData.Remove.ModeDuration);
            UpdateTimerLabel(player, Mathf.CeilToInt(state.ExpiresAt - Time.realtimeSinceStartup));
        }

        /// <summary>
        /// API-хук: проверяет, может ли игрок удалить указанный объект.
        /// Постусловие: null — удаление разрешено; строка — причина запрета.
        /// </summary>
        [HookMethod("canRemove")]
        private object? canRemove(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null)
                return "Некорректные аргументы.";
            return ValidateRemove(player, entity, IsAdmin(player));
        }

        /// <summary>API-хук: true, если у игрока сейчас включен режим удаления.</summary>
        [HookMethod("OnRemoveActivate")]
        private bool OnRemoveActivate(ulong playerId) => activeRemovers.ContainsKey(playerId);

        /// <summary>API-хук: выключает режим удаления игрока, если он включен.</summary>
        [HookMethod("RemoveDeativate")]
        private void RemoveDeativate(ulong playerId) => Deactivate(playerId);

        /// <summary>
        /// Полная валидация удаления. Админский обход (adminBypass) снимает только проверки
        /// владения и лимита времени; запреты по shortname, игрокам и наполнению ящиков действуют всегда.
        /// </summary>
        private string? ValidateRemove(BasePlayer player, BaseEntity entity, bool adminBypass)
        {
            if (entity.IsDestroyed)
                return Msg(player, "AlreadyDestroyed");
            if (entity is BasePlayer)
                return Msg(player, "CannotRemovePlayers");
            if (IsBlockedShortname(entity))
                return Msg(player, "BlockedShortname");

            if (!adminBypass)
            {
                // CHANGE: Вместо общего отказа возвращается конкретная причина (нет шкафа, нет авторизации и т.д.).
                var accessReason = GetAccessDenyReason(player, entity);
                if (accessReason != null)
                    return accessReason;

                // CHANGE: Проверки разнесены по вложенным блокам — использование out-переменной placed
                // внутри длинной цепочки && вызывало CS0165 (анализ определённого присваивания).
                var main = configData.Remove;
                if (
                    main.EnablePlaceTimeLimit
                    && !permission.UserHasPermission(player.UserIDString, main.IgnorePermission)
                )
                {
                    if (
                        placedTimes.TryGetValue(entity.net.ID, out var placed)
                        && Time.realtimeSinceStartup - placed > main.PlaceTimeLimit
                    )
                        return Msg(player, "PlaceTimeLimit");
                }
            }

            if (configData.Remove.BlockWithItems
                && entity is StorageContainer container
                && HasAnyItems(container))
                return Msg(player, "ContainerNotEmpty");

            // CHANGE: Карьер: нельзя удалить с работающим двигателем и с ресурсами/топливом
            // CHANGE: внутри — иначе подбор возвращал предмет, а добытые ресурсы и топливо терялись.
            if (entity is MiningQuarry quarry)
            {
                // CHANGE: Замок RExtendedLocking (Slot.Lock на бункере hopperoutput) блокирует
                // CHANGE: удаление карьера: иначе Kill() карьера уничтожал/осиротял вешанный замок.
                if (GetQuarryLock(quarry) != null)
                    return Msg(player, "QuarryLocked");
                if (quarry.IsEngineOn())
                    return Msg(player, "QuarryRunning");
                // CHANGE: Остаток топлива живёт в pendingWork: бочка списывается из бака ЦЕЛИКОМ
                // CHANGE: при первом цикле (FuelCheck → UseItem), поэтому пустой бак не означает,
                // CHANGE: что топливо израсходовано — без этой проверки карьер подбирался бы
                // CHANGE: с недоизрасходованным дизелем, теряя остаток.
                if (quarry.pendingWork > 0f)
                    return Msg(player, "QuarryFuelNotSpent");
                if (configData.Remove.BlockWithItems)
                {
                    // CHANGE: Дизель и добытые ресурсы различаются: у каждого своя причина отказа.
                    var quarryReason = QuarryContentDenyKey(quarry);
                    if (quarryReason != null)
                        return Msg(player, quarryReason);
                }
            }

            return null;
        }

        /// <summary>
        /// Ищет замок на бункере выхода ресурсов карьера (дочерний контейнер hopperoutput,
        /// слот Slot.Lock) — именно так вешает замки на карьеры плагин RExtendedLocking.
        /// Предусловие: quarry — живой карьер (не IsDestroyed).
        /// Постусловие: null — замка нет; иначе найденный BaseLock (CodeLock/KeyLock).
        /// Сложность: O(k), k — число дочерних сущностей карьера, O(1) по памяти.
        /// </summary>
        private static BaseLock? GetQuarryLock(MiningQuarry quarry)
        {
            foreach (var child in quarry.children)
            {
                if (
                    child is StorageContainer hopper
                    && hopper.ShortPrefabName == "hopperoutput"
                )
                    return hopper.GetSlot(BaseEntity.Slot.Lock) as BaseLock;
            }
            return null;
        }

        /// <summary>
        /// Определяет причину отказа по содержимому контейнеров карьера.
        /// Инвариант: у карьера ровно два легитимных хранилища — fuelstorage (дизель) и
        /// hopperoutput (ресурсы); проверяется ТОЛЬКО whitelist по shortname префаба.
        /// Прочие дочерние StorageContainer игнорируются: BaseOven наследуется от
        /// StorageContainer, и декоративный костёр CustomQuarry (пустой или служебный)
        /// иначе давал ложное «в объекте есть предметы» на свежем карьере.
        /// Постусловие: null — хранилища пусты; иначе ключ локализации причины.
        /// Сложность: O(k), k — число дочерних контейнеров карьера, O(1) по памяти.
        /// </summary>
        private string? QuarryContentDenyKey(MiningQuarry quarry)
        {
            foreach (var quarryContainer in quarry.GetComponentsInChildren<StorageContainer>())
            {
                if (quarryContainer == null || !HasAnyItems(quarryContainer))
                    continue;
                var shortName = quarryContainer.ShortPrefabName;
                // CHANGE: Частично израсходованный дизель — отдельное сообщение: игрок мог
                // CHANGE: выключить карьер с недоизрасходованной бочкой, предметы тут не при чём.
                if (shortName.Contains("fuel"))
                    return "QuarryHasFuel";
                if (shortName.Contains("hopper") || shortName.Contains("output"))
                    return "ContainerNotEmpty";
            }
            return null;
        }

        /// <summary>
        /// Проверяет сущность по чёрному списку ("x*" — префиксный запрет).
        /// Совпадением считается shortname КАК энтити (ShortPrefabName), так и предмета-деплоя:
        /// у ряда объектов имена различаются (lantern → lantern.deployed,
        /// small.oil.refinery → refinery_small_deployed), из-за чего записи вида предмета
        /// раньше не срабатывали и объекты поднимались ремувом (1.6.6).
        /// Сложность: O(n) по записям списка, O(1) на запись (кэш deployableItems).
        /// </summary>
        private bool IsBlockedShortname(BaseEntity entity)
        {
            var itemName = entity.prefabID != 0
                && deployableItems.TryGetValue(entity.prefabID, out var definition)
                ? definition.shortname
                : null;
            foreach (var raw in configData.Remove.BlockedShortnames)
            {
                var entry = raw?.Trim();
                if (string.IsNullOrEmpty(entry))
                    continue;
                if (
                    MatchesBlockedEntry(entity.ShortPrefabName, entry)
                    || (itemName != null && MatchesBlockedEntry(itemName, entry))
                )
                    return true;
            }
            return false;
        }

        /// <summary>Точное или префиксное (запись "x*") совпадение имени с записью списка.</summary>
        private static bool MatchesBlockedEntry(string name, string entry) =>
            entry.EndsWith("*", StringComparison.Ordinal)
                ? name.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase)
                : name.Equals(entry, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Определяет причину отказа в доступе к объекту: владение, авторизация в шкафу, команда/соклановцы.
        /// Постусловие: null — доступ разрешён; строка — конкретная человекочитаемая причина отказа.
        /// </summary>
        private string? GetAccessDenyReason(BasePlayer player, BaseEntity entity)
        {
            var main = configData.Remove;
            var own = entity.OwnerID == player.userID;
            var priv = entity.GetBuildingPrivilege();
            var authed = priv != null && priv.IsAuthed(player);

            if (own)
            {
                if (main.AllowOwnWithoutAuth || authed)
                    return null;
                return priv == null
                    ? Msg(player, "OwnNoCupboard")
                    : Msg(player, "OwnNotAuthed");
            }

            if (authed && main.AllowOthersWithAuth)
                return null;
            // CHANGE: «Соклановцы» (клановый стол, Rust.Clans) и «Team» (нативные команды,
            // CHANGE: RelationshipManager) — две РАЗНЫЕ системы: каждая проверяется своим
            // CHANGE: предикатом (SameClan/SameTeam) и своим флагом конфигурации.
            if (main.AllowClanmates && SameClan(player, entity.OwnerID))
                return null;
            if (main.AllowTeam && SameTeam(player, entity.OwnerID))
                return null;

            // CHANGE: Причина отказа конкретизируется: соклановцу — про клан, товарищу по
            // CHANGE: команде — про команду (раньше любая из этих ситуаций отдавала TeamRemovalDisabled).
            if (SameClan(player, entity.OwnerID))
                return Msg(player, "ClanRemovalDisabled");
            if (SameTeam(player, entity.OwnerID))
                return Msg(player, "TeamRemovalDisabled");
            if (authed)
                return Msg(player, "OthersRemovalDisabled");
            return Msg(player, "NotOwner");
        }

        /// <summary>
        /// Проверяет, состоит ли владелец объекта в одном клане с игроком (клановый стол Rust,
        /// сборка Rust.Clans: BasePlayer.clanId/serverClan + ClanManager.ServerInstance.Backend).
        /// Инвариант: клан берётся у онлайн-игрока — BasePlayer.serverClan обновляется игрой
        /// при входе (LoadClanInfo) и при каждом изменении состава клана; владелец объекта
        /// при этом может быть офлайн (sleeping), поэтому его членство проверяется прямым
        /// перебором списка Members клана игрока.
        /// Предусловие: player — подключённый игрок (не NPC/бот).
        /// Постусловие: true — только если оба состоят в одном клане.
        /// Сложность: O(m) по времени, O(1) по памяти; m — размер клана (ограничен maxMemberCount).
        /// </summary>
        private bool SameClan(BasePlayer player, ulong ownerId)
        {
            if (ownerId == 0UL || player.clanId == 0L)
                return false;
            var clan = player.serverClan;
            if (clan == null)
            {
                // CHANGE: Fallback: serverClan может быть не загружен в момент входа игрока —
                // CHANGE: достаём клан синхронным запросом по clanId (TryGet — словарный поиск
                // CHANGE: бэкенда кланов, без асинхронности), иначе проверка корректно падает.
                var backend = ClanManager.ServerInstance?.Backend;
                if (backend == null || !backend.TryGet(player.clanId, out clan))
                    return false;
            }
            foreach (var member in clan.Members)
            {
                if (member.SteamId == ownerId)
                    return true;
            }
            return false;
        }

        /// <summary>Проверяет, состоит ли владелец объекта в одной нативной команде с игроком.</summary>
        private bool SameTeam(BasePlayer player, ulong ownerId)
        {
            if (player.currentTeam == 0 || ownerId == 0)
                return false;
            var team = RelationshipManager.ServerInstance?.FindTeam(player.currentTeam);
            return team != null && team.members.Contains(ownerId);
        }

        /// <summary>Проверяет, есть ли в инвентаре контейнера хоть один предмет.</summary>
        private bool HasAnyItems(StorageContainer container) =>
            container.inventory != null
            && container.inventory.itemList != null
            && container.inventory.itemList.Count > 0;

        /// <summary>
        /// Возвращает игроку ресурсы/объект за удаляемую сущность согласно конфигурации.
        /// Строительные блоки — ресурсы с процентом постройки; ящики — содержимое по процентам;
        /// остальные объекты — сам предмет (при включенном возврате) или ресурсы его крафта
        /// с процентом Items (при выключенном).
        /// </summary>
        private void RefundEntity(BasePlayer player, BaseEntity entity)
        {
            var main = configData.Remove;

            if (entity is BuildingBlock block)
            {
                RefundBuildingBlock(player, block);
                return;
            }

            var definition = ResolveDeployableItem(entity);
            if (definition == null)
                return;

            if (entity is StorageContainer container)
                RefundContainerContents(player, container);

            if (main.ReturnDeployables)
                GiveEntityItem(player, entity, definition);
            else
                RefundItemResources(player, definition);
        }

        /// <summary>
        /// Определяет предмет, соответствующий удаляемому объекту.
        /// Сначала кэш «prefabID → предмет» через ItemModDeployable (покрывает DLC-объекты,
        /// у которых shortname энтити отличается от shortname предмета), затем fallback по shortname.
        /// Сложность: O(1).
        /// </summary>
        private ItemDefinition? ResolveDeployableItem(BaseEntity entity)
        {
            if (entity.prefabID != 0 && deployableItems.TryGetValue(entity.prefabID, out var byPrefab))
                return byPrefab;
            return ItemManager.FindItemDefinition(entity.ShortPrefabName);
        }

        /// <summary>
        /// Строит кэш «prefabID энтити → ItemDefinition» по всем предметам с ItemModDeployable.
        /// Вызывается один раз при инициализации; сложность O(m), m — число предметов в игре.
        /// </summary>
        private void BuildDeployableItemCache()
        {
            deployableItems.Clear();
            foreach (var definition in ItemManager.itemList)
            {
                if (definition == null)
                    continue;
                var mod = definition.GetComponent<ItemModDeployable>();
                if (mod?.entityPrefab == null || !mod.entityPrefab.isValid)
                    continue;
                deployableItems[mod.entityPrefab.resourceID] = definition;
            }
        }

        /// <summary>
        /// Возвращает ресурсы крафта удаляемого объекта (когда возврат объектов выключен).
        /// Инвариант: без исследованного чертежа у предмета ингридиентов нет — возврат пропускается.
        /// </summary>
        private void RefundItemResources(BasePlayer player, ItemDefinition definition)
        {
            var blueprint = ItemManager.FindBlueprint(definition);
            if (blueprint?.ingredients == null)
                return;

            var percent = Mathf.Clamp(configData.Remove.ItemsRefundPercent, 0f, 1f);
            foreach (var ingredient in blueprint.ingredients)
                GiveRefund(player, ingredient.itemid, Mathf.CeilToInt(ingredient.amount * percent));
        }

        /// <summary>Возвращает ресурсы строительного блока по стоимости текущего грейда.</summary>
        private void RefundBuildingBlock(BasePlayer player, BuildingBlock block)
        {
            var definition = block.blockDefinition;
            if (definition?.grades == null)
                return;
            var gradeIndex = Mathf.Clamp((int)block.grade, 0, definition.grades.Length - 1);
            var grade = definition.grades[gradeIndex];
            if (grade == null)
                return;

            var percent = Mathf.Clamp(configData.Remove.BuildingRefundPercent, 0f, 1f);
            foreach (var cost in grade.CostToBuild())
                GiveRefund(player, cost.itemid, Mathf.CeilToInt(cost.amount * percent));
        }

        /// <summary>
        /// Раздаёт содержимое удаляемого ящика: ресурсы — по проценту контейнеров,
        /// остальные предметы — по проценту Items.
        /// </summary>
        private void RefundContainerContents(BasePlayer player, StorageContainer container)
        {
            if (container.inventory?.itemList == null)
                return;

            var main = configData.Remove;
            foreach (var stored in container.inventory.itemList.ToArray())
            {
                if (stored?.info == null)
                    continue;
                var percent = stored.info.category == ItemCategory.Resources
                    ? Mathf.Clamp(main.ContainerResourceDropPercent, 0f, 1f)
                    : Mathf.Clamp(main.ItemsRefundPercent, 0f, 1f);
                var amount = Mathf.CeilToInt(stored.amount * percent);
                if (amount <= 0)
                    continue;
                GiveRefund(player, stored.info.itemid, amount, stored.skin);
            }
        }

        /// <summary>Создаёт и выдаёт игроку предмет, соответствующий удаляемому объекту.</summary>
        private void GiveEntityItem(BasePlayer player, BaseEntity entity, ItemDefinition definition)
        {
            var item = ItemManager.Create(definition, 1, entity.skinID);
            if (item == null)
                return;
            ApplyConditionLoss(item);
            player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
        }

        /// <summary>Выдаёт игроку N предметов по ID с применением потери прочности.</summary>
        private void GiveRefund(BasePlayer player, int itemId, int amount, ulong skinId = 0UL)
        {
            if (amount <= 0)
                return;
            var item = ItemManager.CreateByItemID(itemId, amount, skinId);
            if (item == null)
                return;
            ApplyConditionLoss(item);
            player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
        }

        /// <summary>Списывает процент прочности с предмета, если потеря включена и предмет имеет прочность.</summary>
        private void ApplyConditionLoss(Item item)
        {
            var main = configData.Remove;
            if (!main.ItemConditionLoss || !item.hasCondition)
                return;
            item.condition = Mathf.Max(
                0f,
                item.condition - item.maxCondition * (Mathf.Clamp(main.ConditionLossPercent, 0f, 100f) / 100f));
        }

        /// <summary>
        /// /remove all: удаляет все строительные блоки и привязанные объекты строения,
        /// к которому относится задетая сущность. Без возврата ресурсов (админская очистка).
        /// Сложность: O(n), n — число сущностей строения.
        /// </summary>
        private void RemoveAllConnected(BasePlayer player, BaseEntity entity)
        {
            // CHANGE: buildingID объявлен на DecayEntity, а BuildingBlock наследуется от StabilityEntity : DecayEntity,
            // CHANGE: поэтому одного паттерна DecayEntity достаточно для всех сущностей строения.
            var buildingId = entity is DecayEntity decay ? decay.buildingID : 0u;
            var building = buildingId != 0u
                ? BuildingManager.server?.GetBuilding(buildingId)
                : null;
            if (building == null)
            {
                Toast(player, Msg(player, "NoBuilding"));
                return;
            }

            // CHANGE: Итерируем по копиям списков, чтобы не модифицировать коллекцию во время обхода.
            foreach (var block in building.buildingBlocks.ToArray())
            {
                if (block != null && !block.IsDestroyed)
                    block.Kill();
            }
            foreach (var decayEntity in building.decayEntities.ToArray())
            {
                if (decayEntity != null && !decayEntity.IsDestroyed)
                    decayEntity.Kill();
            }
            // CHANGE: Чат-итог и консольный лог убраны — исчезновение строения и есть результат.
        }

        #endregion Удаление

        #region GUI

        /// <summary>Рисует индикатор режима: заголовок, таймер и подсказка. Все значения — из конфига.</summary>
        private void ShowRemoveUi(BasePlayer player, RemoveMode mode, int secondsLeft)
        {
            CuiHelper.DestroyUi(player, UiRoot);
            var gui = configData.Gui;
            if (!gui.Enabled || player == null)
                return;

            var container = new CuiElementContainer();

            // CHANGE: CuiPanel заменён на CuiElement: в Carbon-совместимом CUI свойство
            // CHANGE: CuiPanel.RectTransform доступно только для чтения.
            container.Add(new CuiElement
            {
                Name = UiRoot,
                Parent = "Hud",
                Components =
                {
                    new CuiImageComponent { Color = gui.Root.BgColor },
                    RootRect(gui.Root),
                },
            });

            container.Add(new CuiElement
            {
                Parent = UiRoot,
                Components =
                {
                    new CuiImageComponent { Color = gui.Root.HeaderBgColor },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "1 1",
                        OffsetMin = $"0 {F(-gui.Root.HeaderHeight)}",
                        OffsetMax = "0 0",
                    },
                },
            });

            container.Add(new CuiElement
            {
                Name = UiTimerLabel,
                Parent = UiRoot,
                Components =
                {
                    new CuiTextComponent
                    {
                        // CHANGE: Таймер сразу создаётся с актуальным остатком времени, а не пустым.
                        Text = Msg(player, "TimeLeft").Replace(
                            "{0}",
                            secondsLeft.ToString(CultureInfo.InvariantCulture)),
                        FontSize = gui.Timer.FontSize,
                        Color = gui.Timer.Color,
                        Align = TextAnchor.MiddleCenter,
                    },
                    TimerRect(gui),
                },
            });

            container.Add(new CuiElement
            {
                Parent = UiRoot,
                Components =
                {
                    new CuiTextComponent
                    {
                        // CHANGE: Текст заголовка берется из локализации и отражает тип активного режима.
                        Text = Msg(player, mode switch
                        {
                            RemoveMode.Admin => "GuiAdminTitle",
                            RemoveMode.All => "GuiAllTitle",
                            _ => "GuiTitle",
                        }),
                        FontSize = gui.Title.FontSize,
                        Color = gui.Title.Color,
                        Align = TextAnchor.MiddleCenter,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "1 1",
                        OffsetMin = $"0 {F(-gui.Root.HeaderHeight)}",
                        OffsetMax = "0 0",
                    },
                },
            });

            container.Add(new CuiElement
            {
                Parent = UiRoot,
                Components =
                {
                    new CuiTextComponent
                    {
                        // CHANGE: Текст подсказки берется из локализации, а не из конфига.
                        Text = Msg(player, "GuiHint"),
                        FontSize = gui.Hint.FontSize,
                        Color = gui.Hint.Color,
                        Align = TextAnchor.MiddleCenter,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 0",
                        OffsetMin = "0 0",
                        OffsetMax = $"0 {F(gui.Hint.Height)}",
                    },
                },
            });

            CuiHelper.AddUi(player, container);
        }

        /// <summary>
        /// Рисует индикатор режима апгрейда: заголовок, выбранный тип с таймером и подсказка.
        /// Все значения — из конфига.
        /// </summary>
        private void ShowUpgradeUi(BasePlayer player, BuildingGrade.Enum grade, int secondsLeft)
        {
            var gui = configData.Gui;
            if (!gui.Enabled || player == null)
                return;
            CuiHelper.DestroyUi(player, UiRoot);

            var gradeName = Msg(player, grade switch
            {
                BuildingGrade.Enum.Wood => "GradeWood",
                BuildingGrade.Enum.Stone => "GradeStone",
                BuildingGrade.Enum.Metal => "GradeMetal",
                _ => "GradeHqm",
            });

            var container = new CuiElementContainer();

            container.Add(new CuiElement
            {
                Name = UiRoot,
                Parent = "Hud",
                Components =
                {
                    new CuiImageComponent { Color = gui.Root.BgColor },
                    RootRect(gui.Root),
                },
            });

            container.Add(new CuiElement
            {
                Parent = UiRoot,
                Components =
                {
                    new CuiImageComponent { Color = gui.Root.HeaderBgColor },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "1 1",
                        OffsetMin = $"0 {F(-gui.Root.HeaderHeight)}",
                        OffsetMax = "0 0",
                    },
                },
            });

            // CHANGE: Имя типа вынесено в заголовок: средняя зона панели слишком мала для двух
            // CHANGE: строк (клиентский текст обрезается по высоте контейнера), лейблы пропадали.
            container.Add(new CuiElement
            {
                Name = UiTimerLabel,
                Parent = UiRoot,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg(player, "TimeLeft").Replace(
                            "{0}",
                            secondsLeft.ToString(CultureInfo.InvariantCulture)),
                        FontSize = gui.Timer.FontSize,
                        Color = gui.Timer.Color,
                        Align = TextAnchor.MiddleCenter,
                    },
                    TimerRect(gui),
                },
            });

            container.Add(new CuiElement
            {
                Parent = UiRoot,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = $"{Msg(player, "GuiUpgradeTitle")}: {gradeName}",
                        FontSize = gui.Title.FontSize,
                        Color = gui.Title.Color,
                        Align = TextAnchor.MiddleCenter,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "1 1",
                        OffsetMin = $"0 {F(-gui.Root.HeaderHeight)}",
                        OffsetMax = "0 0",
                    },
                },
            });

            container.Add(new CuiElement
            {
                Parent = UiRoot,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg(player, "GuiUpgradeHint"),
                        FontSize = gui.Hint.FontSize,
                        Color = gui.Hint.Color,
                        Align = TextAnchor.MiddleCenter,
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 0",
                        OffsetMin = "0 0",
                        OffsetMax = $"0 {F(gui.Hint.Height)}",
                    },
                },
            });

            CuiHelper.AddUi(player, container);
        }

        /// <summary>RectTransform корневой панели: якорь в центре экрана + офсеты из конфига.</summary>
        private static CuiRectTransformComponent RootRect(RootPanelConfig panel)
        {
            var halfWidth = panel.Width / 2f;
            var halfHeight = panel.Height / 2f;
            return new CuiRectTransformComponent
            {
                AnchorMin = "0.5 0.5",
                AnchorMax = "0.5 0.5",
                OffsetMin = $"{F(panel.OffsetX - halfWidth)} {F(panel.OffsetY - halfHeight)}",
                OffsetMax = $"{F(panel.OffsetX + halfWidth)} {F(panel.OffsetY + halfHeight)}",
            };
        }

        /// <summary>RectTransform таймера: пространство между блоком заголовка и подсказкой.</summary>
        private static CuiRectTransformComponent TimerRect(GuiSettings gui) => new()
        {
            AnchorMin = "0 0",
            AnchorMax = "1 1",
            OffsetMin = $"0 {F(gui.Hint.Height)}",
            OffsetMax = $"0 {F(-gui.Root.HeaderHeight)}",
        };

        /// <summary>Секундный тикер: обновляет обратные отсчёты и гасит режимы по истечении времени.</summary>
        private void UpdateTickers()
        {
            var now = Time.realtimeSinceStartup;

            // CHANGE: Подчистка истёкших записей встроенной блокировки (защита от роста словарей).
            if (builtinRaidBlocked.Count > 0)
                PruneExpired(builtinRaidBlocked, now);
            if (builtinCombatBlocked.Count > 0)
                PruneExpired(builtinCombatBlocked, now);

            // CHANGE: Тикер режима апгрейда: бездействие ведёт обратный отсчёт до выключения режима.
            foreach (var pair in upgradeGrades.ToArray())
            {
                var player = BasePlayer.FindByID(pair.Key);
                if (player == null || !player.IsConnected)
                {
                    DeactivateUpgrade(pair.Key, destroyUi: false);
                    continue;
                }

                var remaining = pair.Value.ExpiresAt - now;
                if (remaining <= 0f)
                {
                    DeactivateUpgrade(pair.Key);
                    continue;
                }

                UpdateUpgradeLabel(player, pair.Value, Mathf.CeilToInt(remaining));
            }

            foreach (var pair in activeRemovers.ToArray())
            {
                var player = BasePlayer.FindByID(pair.Key);
                if (player == null || !player.IsConnected)
                {
                    Deactivate(pair.Key, destroyUi: false);
                    continue;
                }

                var remaining = pair.Value.ExpiresAt - now;
                if (remaining <= 0f)
                {
                    // CHANGE: Чат-уведомление об истечении убрано — панель исчезает, это и есть сигнал.
                    Deactivate(pair.Key);
                    continue;
                }

                UpdateTimerLabel(player, Mathf.CeilToInt(remaining));
            }
        }

        /// <summary>Обновляет лейбл таймера режима апгрейда без мерцания (Update = true).</summary>
        private void UpdateUpgradeLabel(BasePlayer player, UpgradeState state, int secondsLeft)
        {
            var gui = configData.Gui;
            if (!gui.Enabled)
                return;

            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = UiTimerLabel,
                Parent = UiRoot,
                Update = true,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg(player, "TimeLeft").Replace("{0}", secondsLeft.ToString(CultureInfo.InvariantCulture)),
                        FontSize = gui.Timer.FontSize,
                        Color = gui.Timer.Color,
                        Align = TextAnchor.MiddleCenter,
                    },
                    TimerRect(gui),
                },
            });
            CuiHelper.AddUi(player, container);
        }

        /// <summary>Обновляет текст таймера без мерцания (Update = true).</summary>
        private void UpdateTimerLabel(BasePlayer player, int secondsLeft)
        {
            var gui = configData.Gui;
            if (!gui.Enabled)
                return;

            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = UiTimerLabel,
                Parent = UiRoot,
                Update = true,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg(player, "TimeLeft").Replace("{0}", secondsLeft.ToString(CultureInfo.InvariantCulture)),
                        FontSize = gui.Timer.FontSize,
                        Color = gui.Timer.Color,
                        Align = TextAnchor.MiddleCenter,
                    },
                    TimerRect(gui),
                },
            });
            CuiHelper.AddUi(player, container);
        }

        /// <summary>Форматирует число в строку CUI с инвариантной культурой.</summary>
        private static string F(float value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        #endregion GUI
    }
}
