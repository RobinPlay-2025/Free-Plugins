using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins
{
    [Info("RAdminMenu", "RustInnovate", "2.0.1")]
    [Description("Современное модульное меню администратора")]
    public class RAdminMenu : RustPlugin
    {
        // CHANGE: Компонент маскирования RectMask2D для аппаратной обрезки UI элементов внутри ScrollView
        private sealed class CuiRectMask2DComponent : ICuiComponent
        {
            public string Type => "UnityEngine.UI.RectMask2D";
        }

        // CHANGE: Ссылки на опциональные плагины для отображения в UserInfo
        [PluginReference]
        private Plugin Economics,
            ServerRewards,
            Clans;

        #region Constants & Permissions


        // CHANGE: Базовые слои пользовательского интерфейса
        private const string LayerMain = "RAdminMenu.Main";
        private const string LayerNavigation = "RAdminMenu.Navigation";
        private const string LayerNavButtons = "RAdminMenu.NavButtons";
        private const string LayerHeader = "RAdminMenu.Header";
        private const string LayerHeaderTitle = "RAdminMenu.HeaderTitle";
        private const string LayerHeaderInfo = "RAdminMenu.HeaderInfo";
        private const string LayerContent = "RAdminMenu.Content";
        private const string LayerContentBody = "RAdminMenu.ContentBody";
        private const string LayerModal = "RAdminMenu.Modal";

        // CHANGE: Внутренняя фиксированная чувствительность скролла для плавной и быстрой прокрутки
        private const float SCROLL_SENSITIVITY = 50f;

        #endregion

        #region State Management

        private static RAdminMenu Instance;

        // CHANGE: Хранилище сессий интерфейса для каждого игрока
        private class AdminSession
        {
            public string CurrentCategory = "quickmenu";
            public string PlayerFilter = "online";
            public string PlayerSearch = "";
            public int PlayerPage = 0;
            public ulong SelectedUserId = 0;
            public string PluginSearch = "";
            public int PluginPage = 0;
            public string PermTargetType = "group"; // "group" or "user"
            public string PermTargetName = "default";
            public string PermSelectedPlugin = "";
            public string PermSearch = "";
            public int PermPage = 0;
            public string ModalType = ""; //"creategroup", "clonegroup"
            public string ModalInput = "";
            public string ModalInputSecondary = "";

            // CHANGE: Ожидающее подтверждения опасное действие (kill/strip_inv/revoke_bp/revoke_bp_granted)
            public string PendingAction = "";

            // CHANGE: Флаг открытого меню — фоновые таймеры обновления UI не шлют элементы, пока меню закрыто
            public bool MenuOpen = false;
            public bool TeleportToMarkerEnabled = false;

            // CHANGE: Поле ввода времени суток для администратора
            public string TimeInput = "12:00";

            // CHANGE: Подкатегория погоды и сохраненные значения пользовательского ввода
            public string WeatherSubCategory = "presets";
            public Dictionary<string, string> WeatherInputs = new Dictionary<string, string>();

            // CHANGE: Активное поле ввода с фокусом для скрытия плейсхолдера
            public string FocusedInput = "";
        }

        private readonly Dictionary<ulong, AdminSession> _sessions =
            new Dictionary<ulong, AdminSession>();
        private readonly HashSet<ulong> _tpMarkerPlayers = new HashSet<ulong>();

        // CHANGE: Хранилище идентификаторов игроков с активным креативным режимом
        private readonly HashSet<ulong> _creativePlayers = new HashSet<ulong>();

        // CHANGE: Снапшот нативных конвар Creative.* на момент включения креатива — для возврата исходных значений
        private bool _creativeConvarsSnapshotTaken;
        private readonly Dictionary<string, bool> _creativeConvarsSnapshot =
            new Dictionary<string, bool>();
        private readonly Dictionary<ulong, SteamInfo> _cachedSteamInfo =
            new Dictionary<ulong, SteamInfo>();

        // CHANGE: Экземпляр Harmony для явного применения/снятия патчей при загрузке/выгрузке плагина
        private HarmonyLib.Harmony _harmony;

        public class SteamInfo
        {
            public string Location { get; set; }
            public string[] Avatars { get; set; }
            public string RegistrationDate { get; set; }
            public string RustHours { get; set; }
        }

        #endregion

        #region Configuration

        // CHANGE: Универсальный класс позиционирования согласно строгим правилам формата
        public class PanelSettings
        {
            [JsonProperty("Высота")]
            public int Height;

            [JsonProperty("Ширина")]
            public int Width;

            [JsonProperty("Вверх/вниз")]
            public int OffsetY;

            [JsonProperty("Влево/вправо")]
            public int OffsetX;
        }

        public class GeneralSettings
        {

            [JsonProperty("Горячая клавиша для открытия (X | F | OFF)")]
            [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
            public ButtonHook ButtonToHook = ButtonHook.X;

            [JsonProperty("Отключить загрузку аватарок Steam")]
            public bool DisablePlayerAvatars = false;

            // CHANGE: ObjectCreationHandling.Replace для предотвращения дублирования избранных плагинов
            [JsonProperty(
                "Список избранных плагинов",
                ObjectCreationHandling = ObjectCreationHandling.Replace
            )]
            public HashSet<string> FavoritePlugins = new HashSet<string>();

            [JsonProperty("Фон затемнения экрана")]
            public string OverlayColor = "0 0 0 0.70";

            // CHANGE: Индекс цвета по умолчанию для построек из контейнеров в креатив-режиме (0 - стандартный)
            [JsonProperty("Цвет контейнеров по умолчанию при постройке (0-15)")]
            public uint DefaultContainerColor = 0;
        }

        // CHANGE: Нативные конвары Creative.* вынесены в конфиг. Игра гейтит каждую креатив-возможность парой
        // (флаг игрока CreativeMode + серверный конвар), поэтому без включения конваров флаг сам по себе ничего не даёт.
        public class CreativeSettings
        {
            [JsonProperty("Бесплатная постройка и улучшение (creative.freebuild)")]
            public bool FreeBuild = true;

            [JsonProperty("Бесплатный ремонт (creative.freerepair)")]
            public bool FreeRepair = true;

            [JsonProperty("Игнорирование проверок размещения (creative.freeplacement)")]
            public bool FreePlacement = true;

            [JsonProperty("Пропуск задержки удержания при установке (creative.bypassholdtoplaceduration)")]
            public bool BypassHoldToPlaceDuration = true;

            [JsonProperty("Безлимитные электрические соединения (creative.unlimitedio)")]
            public bool UnlimitedIo = true;

            // CHANGE: По умолчанию выключено — это failsafe-конвар игры; клиентские команды always-on вдобавок требуют прав администратора
            [JsonProperty("Разрешить переключение Always-On (creative.alwaysonenabled)")]
            public bool AlwaysOn = false;
        }

        public class NavigationHeaderSettings
        {
            // CHANGE: Текст заголовка навигации вынесен в локализацию (NAV_HEADER_TITLE)
            [JsonProperty("Цвет заголовка")]
            public string TextColor = "0.25 0.69 1 1";

            [JsonProperty("Размер шрифта заголовка")]
            // CHANGE: 20 вместо 18 — мелкий кегль растрируется клиентом с пиксельными ступеньками
            public int FontSize = 20;

            [JsonProperty("Высота блока заголовка")]
            public int Height = 42;
        }

        public class NavigationButtonSettings
        {
            [JsonProperty("Ширина кнопки навигации")]
            public int Width = 174;

            [JsonProperty("Высота кнопки навигации")]
            public int Height = 42;

            [JsonProperty("Отступ сверху первой кнопки")]
            public int StartY = 240;

            [JsonProperty("Отступ между кнопками навигации")]
            public int Spacing = 8;

            [JsonProperty("Размер шрифта кнопок навигации")]
            public int FontSize = 14;

            [JsonProperty("Цвет текста кнопок навигации")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет активной кнопки")]
            public string ActiveColor = "0.25 0.69 1 0.85";

            [JsonProperty("Цвет неактивной кнопки")]
            public string InactiveColor = "0.22 0.23 0.25 0.85";
        }

        public class NavigationSettings
        {
            [JsonProperty("Позиция панели навигации")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 190,
                Height = 560,
                // CHANGE: Центрирование всего меню (навигация + контент) относительно экрана
                OffsetX = -347,
                OffsetY = 0,
            };

            [JsonProperty("Фон панели навигации")]
            public string BackgroundColor = "0.14 0.15 0.16 0.92";

            [JsonProperty("Настройки заголовка")]
            public NavigationHeaderSettings Header = new NavigationHeaderSettings();

            [JsonProperty("Настройки кнопок")]
            public NavigationButtonSettings Buttons = new NavigationButtonSettings();
        }

        public class HeaderTitleSettings
        {
            [JsonProperty("Отступ заголовка слева (OffsetX)")]
            public int OffsetX = 15;

            [JsonProperty("Размер шрифта заголовка")]
            public int FontSize = 20;

            [JsonProperty("Цвет текста заголовка")]
            public string TextColor = "0.25 0.69 1 1";
        }

        public class HeaderInfoSettings
        {
            [JsonProperty("Отступ информации справа (OffsetX)")]
            public int OffsetX = -15;

            [JsonProperty("Размер шрифта информации об онлайне")]
            public int FontSize = 13;

            [JsonProperty("Цвет текста информации об онлайне")]
            public string TextColor = "0.70 0.72 0.75 1";
        }

        public class HeaderSettings
        {
            [JsonProperty("Позиция панели заголовка")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 690,
                Height = 48,
                // CHANGE: Центрирование всего меню (навигация + контент) относительно экрана
                OffsetX = 98,
                OffsetY = 256,
            };

            [JsonProperty("Фон панели заголовка")]
            public string BackgroundColor = "0.14 0.15 0.16 0.92";

            [JsonProperty("Заголовок раздела")]
            public HeaderTitleSettings Title = new HeaderTitleSettings();

            [JsonProperty("Информационный блок (онлайн, FPS)")]
            public HeaderInfoSettings Info = new HeaderInfoSettings();

            [JsonProperty("Интервал динамического обновления FPS и онлайна (сек)")]
            public float UpdateInterval = 1f;
        }

        public class ContentSettings
        {
            [JsonProperty("Позиция панели контента")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 690,
                Height = 504,
                // CHANGE: Центрирование всего меню (навигация + контент) относительно экрана
                OffsetX = 98,
                OffsetY = -28,
            };

            [JsonProperty("Фон панели контента")]
            public string BackgroundColor = "0.16 0.17 0.18 0.92";
        }

        public class QuickMenuTeleportCardSettings
        {
            [JsonProperty("Позиция карточки")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 660,
                Height = 100,
                OffsetX = 0,
                OffsetY = 168,
            };

            [JsonProperty("Фон карточки")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Отступ заголовка по X (слева)")]
            public int TitleOffsetX = 16;

            [JsonProperty("Отступ заголовка по Y (от верха)")]
            public int TitleOffsetY = 30;

            [JsonProperty("Ширина заголовка")]
            public int TitleWidth = 300;

            [JsonProperty("Высота заголовка")]
            public int TitleHeight = 22;

            [JsonProperty("Размер шрифта заголовка")]
            public int TitleFontSize = 13;

            [JsonProperty("Цвет заголовка")]
            public string TitleColor = "0.25 0.69 1 1";

            [JsonProperty("Ширина кнопки")]
            public int ButtonWidth = 202;

            [JsonProperty("Высота кнопки")]
            public int ButtonHeight = 42;

            [JsonProperty("Отступ кнопок по Y (от центра карточки)")]
            public int ButtonsOffsetY = -12;

            [JsonProperty("Отступ между кнопками по X")]
            public int ButtonSpacingX = 12;

            [JsonProperty("Размер шрифта кнопок")]
            public int ButtonFontSize = 13;

            [JsonProperty("Цвет текста кнопок")]
            public string ButtonTextColor = "1 1 1 1";

            [JsonProperty("Цвет кнопки")]
            public string ButtonDefaultColor = "0.22 0.23 0.25 0.85";

            [JsonProperty("Цвет активной кнопки (Метка)")]
            public string ButtonActiveColor = "0.25 0.69 1 0.85";
        }

        public class QuickMenuActionsCardSettings
        {
            [JsonProperty("Позиция карточки")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 660,
                Height = 100,
                OffsetX = 0,
                OffsetY = 56,
            };

            [JsonProperty("Фон карточки")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Отступ заголовка по X (слева)")]
            public int TitleOffsetX = 16;

            [JsonProperty("Отступ заголовка по Y (от верха)")]
            public int TitleOffsetY = 30;

            [JsonProperty("Ширина заголовка")]
            public int TitleWidth = 300;

            [JsonProperty("Высота заголовка")]
            public int TitleHeight = 22;

            [JsonProperty("Размер шрифта заголовка")]
            public int TitleFontSize = 13;

            [JsonProperty("Цвет заголовка")]
            public string TitleColor = "0.25 0.69 1 1";

            [JsonProperty("Ширина кнопки")]
            public int ButtonWidth = 202;

            [JsonProperty("Высота кнопки")]
            public int ButtonHeight = 42;

            [JsonProperty("Отступ кнопок по Y (от центра карточки)")]
            public int ButtonsOffsetY = -12;

            [JsonProperty("Отступ между кнопками по X")]
            public int ButtonSpacingX = 12;

            [JsonProperty("Размер шрифта кнопок")]
            public int ButtonFontSize = 13;

            [JsonProperty("Цвет текста кнопок")]
            public string ButtonTextColor = "1 1 1 1";

            [JsonProperty("Цвет кнопки Вылечить себя")]
            public string HealButtonColor = "0.28 0.65 0.32 0.85";

            [JsonProperty("Цвет кнопки Починить все")]
            public string RepairButtonColor = "0.25 0.69 1 0.85";

            [JsonProperty("Цвет кнопки Очистить инвентарь")]
            public string ClearInvButtonColor = "0.75 0.25 0.25 0.85";

            // CHANGE: Цвета кнопки креатив-режима (обычное и активное состояние) вынесены в конфиг
            [JsonProperty("Цвет кнопки Креатив-режим")]
            public string CreativeButtonColor = "0.25 0.69 1 0.85";

            [JsonProperty("Цвет активной кнопки Креатив-режим")]
            public string CreativeActiveButtonColor = "0.28 0.65 0.32 0.85";
        }

        public class QuickMenuEventsCardSettings
        {
            [JsonProperty("Позиция карточки")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 660,
                Height = 100,
                OffsetX = 0,
                OffsetY = -56,
            };

            [JsonProperty("Фон карточки")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Отступ заголовка по X (слева)")]
            public int TitleOffsetX = 16;

            [JsonProperty("Отступ заголовка по Y (от верха)")]
            public int TitleOffsetY = 30;

            [JsonProperty("Ширина заголовка")]
            public int TitleWidth = 300;

            [JsonProperty("Высота заголовка")]
            public int TitleHeight = 22;

            [JsonProperty("Размер шрифта заголовка")]
            public int TitleFontSize = 13;

            [JsonProperty("Цвет заголовка")]
            public string TitleColor = "0.25 0.69 1 1";

            [JsonProperty("Ширина кнопки")]
            public int ButtonWidth = 202;

            [JsonProperty("Высота кнопки")]
            public int ButtonHeight = 42;

            [JsonProperty("Отступ кнопок по Y (от центра карточки)")]
            public int ButtonsOffsetY = -12;

            [JsonProperty("Отступ между кнопками по X")]
            public int ButtonSpacingX = 12;

            [JsonProperty("Размер шрифта кнопок")]
            public int ButtonFontSize = 13;

            [JsonProperty("Цвет текста кнопок")]
            public string ButtonTextColor = "1 1 1 1";

            [JsonProperty("Цвет кнопок событий")]
            public string ButtonColor = "0.85 0.55 0.20 0.85";
        }

        public class QuickMenuCurrentTimeSettings
        {
            [JsonProperty("Отступ текущего времени по X (справа)")]
            public int OffsetX = -16;

            [JsonProperty("Отступ текущего времени по Y (от верха)")]
            public int OffsetY = 30;

            [JsonProperty("Ширина блока текущего времени")]
            public int Width = 260;

            [JsonProperty("Высота блока текущего времени")]
            public int Height = 22;

            [JsonProperty("Размер шрифта текущего времени")]
            public int FontSize = 13;

            [JsonProperty("Цвет текста текущего времени")]
            public string TextColor = "0.70 0.72 0.75 1";
        }

        public class QuickMenuTimePresetButtonsSettings
        {
            [JsonProperty("Ширина кнопки пресета")]
            public int Width = 202;

            [JsonProperty("Высота кнопки пресета")]
            public int Height = 42;

            [JsonProperty("Отступ кнопок по Y (от центра карточки)")]
            public int OffsetY = -12;

            [JsonProperty("Отступ между кнопками по X")]
            public int SpacingX = 12;

            [JsonProperty("Размер шрифта кнопок")]
            public int FontSize = 13;

            [JsonProperty("Цвет текста кнопок")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет фона кнопок пресетов")]
            public string BackgroundColor = "0.22 0.23 0.25 0.85";
        }

        public class QuickMenuTimeInputSettings
        {
            [JsonProperty("Отступ блока ввода по X (от центра)")]
            public int OffsetX = 214;

            [JsonProperty("Ширина поля ввода времени")]
            public int Width = 104;

            [JsonProperty("Высота поля ввода времени")]
            public int Height = 42;

            [JsonProperty("Размер шрифта поля ввода")]
            public int FontSize = 13;

            [JsonProperty("Максимальная длина ввода символов")]
            public int CharsLimit = 10;

            [JsonProperty("Внутренний отступ поля ввода по X")]
            public int PaddingX = 0;

            [JsonProperty("Внутренний отступ поля ввода по Y")]
            public int PaddingY = 0;

            [JsonProperty("Цвет фона поля ввода")]
            public string BackgroundColor = "0.10 0.10 0.11 0.90";

            [JsonProperty("Цвет текста в поле ввода")]
            public string TextColor = "1 1 1 1";

            // CHANGE: Настройка цвета текста плейсхолдера поля ввода времени
            [JsonProperty("Цвет текста подсказки (плейсхолдера)")]
            public string PlaceholderColor = "0.60 0.62 0.65 0.60";
        }

        public class QuickMenuTimeApplyButtonSettings
        {
            [JsonProperty("Ширина кнопки применить")]
            public int Width = 94;

            [JsonProperty("Высота кнопки применить")]
            public int Height = 42;

            [JsonProperty("Отступ кнопки применить от поля ввода")]
            public int SpacingX = 4;

            [JsonProperty("Размер шрифта кнопки применить")]
            public int FontSize = 12;

            [JsonProperty("Цвет фона кнопки применить")]
            public string BackgroundColor = "0.28 0.65 0.32 0.85";

            [JsonProperty("Цвет текста кнопки применить")]
            public string TextColor = "1 1 1 1";
        }

        public class QuickMenuTimeCardSettings
        {
            [JsonProperty("Позиция карточки")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 660,
                Height = 100,
                OffsetX = 0,
                OffsetY = -168,
            };

            [JsonProperty("Фон карточки")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Отступ заголовка по X (слева)")]
            public int TitleOffsetX = 16;

            [JsonProperty("Отступ заголовка по Y (от верха)")]
            public int TitleOffsetY = 30;

            [JsonProperty("Ширина заголовка")]
            public int TitleWidth = 250;

            [JsonProperty("Высота заголовка")]
            public int TitleHeight = 22;

            [JsonProperty("Размер шрифта заголовка")]
            public int TitleFontSize = 13;

            [JsonProperty("Цвет заголовка")]
            public string TitleColor = "0.25 0.69 1 1";

            [JsonProperty("Блок текущего времени")]
            public QuickMenuCurrentTimeSettings CurrentTime = new QuickMenuCurrentTimeSettings();

            [JsonProperty("Кнопки пресетов")]
            public QuickMenuTimePresetButtonsSettings Presets =
                new QuickMenuTimePresetButtonsSettings();

            [JsonProperty("Поле ввода времени")]
            public QuickMenuTimeInputSettings Input = new QuickMenuTimeInputSettings();

            [JsonProperty("Кнопка применить")]
            public QuickMenuTimeApplyButtonSettings ApplyButton =
                new QuickMenuTimeApplyButtonSettings();
        }

        public class QuickMenuWeatherDetailButtonSettings
        {
            [JsonProperty("Ширина кнопки детальных настроек")]
            public int Width = 130;

            [JsonProperty("Высота кнопки детальных настроек")]
            public int Height = 22;

            [JsonProperty("Отступ кнопки детальных настроек по X (справа)")]
            public int OffsetX = -16;

            [JsonProperty("Отступ кнопки детальных настроек по Y (от центра по Y)")]
            public int OffsetY = 30;

            [JsonProperty("Размер шрифта")]
            public int FontSize = 12;

            [JsonProperty("Цвет текста")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет фона")]
            public string BackgroundColor = "0.25 0.69 1 0.85";
        }

        public class QuickMenuWeatherButtonsSettings
        {
            [JsonProperty("Ширина кнопки")]
            public int Width = 96;

            [JsonProperty("Высота кнопки")]
            public int Height = 42;

            [JsonProperty("Отступ кнопок по Y (от центра карточки)")]
            public int OffsetY = -12;

            [JsonProperty("Отступ между кнопками по X")]
            public int SpacingX = 10;

            [JsonProperty("Размер шрифта кнопок")]
            public int FontSize = 12;

            [JsonProperty("Цвет текста кнопок")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет кнопки Ясно")]
            public string ClearColor = "0.25 0.69 1 0.85";

            [JsonProperty("Цвет кнопки Дождь")]
            public string RainColor = "0.22 0.45 0.75 0.85";

            [JsonProperty("Цвет кнопки Туман")]
            public string FogColor = "0.45 0.50 0.55 0.85";

            [JsonProperty("Цвет кнопки Гроза")]
            public string StormColor = "0.55 0.35 0.70 0.85";

            [JsonProperty("Цвет кнопки Сброс")]
            public string ResetColor = "0.80 0.35 0.25 0.90";

            // CHANGE: Цвет кнопки Отчет удалён вместе с кнопкой
        }

        public class QuickMenuWeatherCardSettings
        {
            [JsonProperty("Позиция карточки")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 660,
                Height = 100,
                OffsetX = 0,
                OffsetY = -280,
            };

            [JsonProperty("Фон карточки")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Отступ заголовка по X (слева)")]
            public int TitleOffsetX = 16;

            [JsonProperty("Отступ заголовка по Y (от верха)")]
            public int TitleOffsetY = 30;

            [JsonProperty("Ширина заголовка")]
            public int TitleWidth = 250;

            [JsonProperty("Высота заголовка")]
            public int TitleHeight = 22;

            [JsonProperty("Размер шрифта заголовка")]
            public int TitleFontSize = 13;

            [JsonProperty("Цвет заголовка")]
            public string TitleColor = "0.25 0.69 1 1";

            [JsonProperty("Кнопка детальных настроек")]
            public QuickMenuWeatherDetailButtonSettings DetailButton =
                new QuickMenuWeatherDetailButtonSettings();

            [JsonProperty("Кнопки погоды")]
            public QuickMenuWeatherButtonsSettings Buttons = new QuickMenuWeatherButtonsSettings();
        }

        public class QuickMenuSettings
        {
            [JsonProperty("Карточка: Телепортация")]
            public QuickMenuTeleportCardSettings TeleportCard = new QuickMenuTeleportCardSettings();

            [JsonProperty("Карточка: Действия администратора")]
            public QuickMenuActionsCardSettings ActionsCard = new QuickMenuActionsCardSettings();

            [JsonProperty("Карточка: События")]
            public QuickMenuEventsCardSettings EventsCard = new QuickMenuEventsCardSettings();

            [JsonProperty("Карточка: Управление временем")]
            public QuickMenuTimeCardSettings TimeCard = new QuickMenuTimeCardSettings();

            // CHANGE: Карточка управления погодой в QuickMenu
            [JsonProperty("Карточка: Управление погодой")]
            public QuickMenuWeatherCardSettings WeatherCard = new QuickMenuWeatherCardSettings();
        }


        public class WeatherTabsSettings
        {
            [JsonProperty("Позиция панели табов")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 660,
                Height = 34,
                OffsetX = 0,
                OffsetY = 180,
            };

            [JsonProperty("Ширина кнопки таба")]
            public int TabWidth = 104;

            [JsonProperty("Высота кнопки таба")]
            public int TabHeight = 32;

            [JsonProperty("Отступ между табами")]
            public int TabSpacingX = 6;

            [JsonProperty("Размер шрифта табов")]
            public int FontSize = 12;

            [JsonProperty("Цвет текста табов")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет активного таба")]
            public string ActiveColor = "0.25 0.69 1 0.90";

            [JsonProperty("Цвет неактивного таба")]
            public string InactiveColor = "0.20 0.21 0.23 0.75";
        }

        public class WeatherPresetsGridSettings
        {
            [JsonProperty("Ширина карточки пресета")]
            public int CardWidth = 210;

            [JsonProperty("Высота карточки пресета")]
            public int CardHeight = 64;

            [JsonProperty("Отступ по X между карточками")]
            public int SpacingX = 14;

            [JsonProperty("Отступ по Y между карточками")]
            public int SpacingY = 14;

            [JsonProperty("Количество колонок в сетке")]
            public int Columns = 3;

            [JsonProperty("Размер шрифта названия пресета")]
            public int TitleFontSize = 13;

            [JsonProperty("Размер шрифта команды/описания")]
            public int SubtitleFontSize = 10;

            [JsonProperty("Цвет текста названия")]
            public string TitleColor = "1 1 1 1";

            [JsonProperty("Цвет текста описания")]
            public string SubtitleColor = "0.70 0.72 0.75 1";

            [JsonProperty("Цвет фона карточки пресета")]
            public string CardBackgroundColor = "0.20 0.21 0.23 0.85";

            [JsonProperty("Цвет фона карточки сброса")]
            public string ResetBackgroundColor = "0.65 0.28 0.25 0.85";

        }

        public class WeatherRowSettings
        {
            [JsonProperty("Ширина строки параметра")]
            public int Width = 660;

            [JsonProperty("Высота строки параметра")]
            public int Height = 52;

            [JsonProperty("Фон строки параметра")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Отступ названия параметра слева (X)")]
            public int LabelOffsetX = 12;

            [JsonProperty("Отступ названия параметра по Y")]
            public int LabelOffsetY = 8;

            [JsonProperty("Ширина блока названия параметра")]
            public int LabelWidth = 200;

            [JsonProperty("Высота блока названия параметра")]
            public int LabelHeight = 18;

            [JsonProperty("Размер шрифта названия")]
            public int TitleFontSize = 12;

            [JsonProperty("Цвет шрифта названия")]
            public string TitleColor = "1 1 1 1";

            [JsonProperty("Отступ ConVar имени слева (X)")]
            public int ConVarOffsetX = 12;

            [JsonProperty("Отступ ConVar имени по Y")]
            public int ConVarOffsetY = -10;

            [JsonProperty("Ширина блока ConVar имени")]
            public int ConVarWidth = 200;

            [JsonProperty("Высота блока ConVar имени")]
            public int ConVarHeight = 14;

            [JsonProperty("Размер шрифта ConVar")]
            public int ConVarFontSize = 10;

            [JsonProperty("Цвет шрифта ConVar")]
            public string ConVarColor = "0.55 0.58 0.62 1";
        }

        public class WeatherQuickButtonsSettings
        {
            [JsonProperty("Отступ кнопок слева (X)")]
            public int OffsetX = 216;

            [JsonProperty("Отступ кнопок по Y")]
            public int OffsetY = 0;

            [JsonProperty("Ширина быстрой кнопки")]
            public int ButtonWidth = 44;

            [JsonProperty("Высота быстрой кнопки")]
            public int ButtonHeight = 32;

            [JsonProperty("Отступ между быстрыми кнопками")]
            public int SpacingX = 4;

            [JsonProperty("Размер шрифта быстрых кнопок")]
            public int FontSize = 10;

            [JsonProperty("Цвет текста быстрых кнопок")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет фона быстрой кнопки")]
            public string BackgroundColor = "0.25 0.27 0.30 0.85";

            [JsonProperty("Цвет кнопки Авто")]
            public string AutoButtonColor = "0.25 0.55 0.85 0.85";
        }

        public class WeatherInputBlockSettings
        {
            [JsonProperty("Отступ блока ввода справа (X)")]
            public int OffsetX = 490;

            [JsonProperty("Отступ блока ввода по Y")]
            public int OffsetY = 0;

            [JsonProperty("Ширина поля ввода")]
            public int Width = 80;

            [JsonProperty("Высота поля ввода")]
            public int Height = 32;

            [JsonProperty("Размер шрифта поля ввода")]
            public int FontSize = 12;

            [JsonProperty("Максимум символов ввода")]
            public int CharsLimit = 8;

            [JsonProperty("Внутренний отступ по X")]
            public int PaddingX = 0;

            [JsonProperty("Внутренний отступ по Y")]
            public int PaddingY = 0;

            [JsonProperty("Цвет фона поля ввода")]
            public string BackgroundColor = "0.10 0.10 0.11 0.90";

            [JsonProperty("Цвет текста поля ввода")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет плейсхолдера")]
            public string PlaceholderColor = "0.60 0.62 0.65 0.60";
        }

        public class WeatherApplyButtonSettings
        {
            [JsonProperty("Отступ кнопки применить справа (X)")]
            public int OffsetX = 576;

            [JsonProperty("Отступ кнопки применить по Y")]
            public int OffsetY = 0;

            [JsonProperty("Ширина кнопки применить")]
            public int Width = 72;

            [JsonProperty("Высота кнопки применить")]
            public int Height = 32;

            [JsonProperty("Размер шрифта кнопки применить")]
            public int FontSize = 11;

            [JsonProperty("Цвет фона кнопки применить")]
            public string BackgroundColor = "0.28 0.65 0.32 0.85";

            [JsonProperty("Цвет текста кнопки применить")]
            public string TextColor = "1 1 1 1";
        }

        public class WeatherManagerSettings
        {
            [JsonProperty("Табы категорий")]
            public WeatherTabsSettings Tabs = new WeatherTabsSettings();

            [JsonProperty("Сетка карточек пресетов")]
            public WeatherPresetsGridSettings PresetsGrid = new WeatherPresetsGridSettings();

            [JsonProperty("Строка параметра")]
            public WeatherRowSettings Row = new WeatherRowSettings();

            [JsonProperty("Кнопки быстрой установки")]
            public WeatherQuickButtonsSettings QuickButtons = new WeatherQuickButtonsSettings();

            [JsonProperty("Поле числового ввода")]
            public WeatherInputBlockSettings Input = new WeatherInputBlockSettings();

            [JsonProperty("Кнопка применить")]
            public WeatherApplyButtonSettings ApplyButton = new WeatherApplyButtonSettings();
        }

        public class PlayerListFilterSettings
        {
            [JsonProperty("Высота кнопок фильтров")]
            public int Height = 32;

            [JsonProperty("Ширина кнопки фильтра")]
            public int Width = 92;

            [JsonProperty("Отступ фильтров сверху (по Y)")]
            public int OffsetY = 215;

            [JsonProperty("Отступ между фильтрами")]
            public int Spacing = 6;

            [JsonProperty("Размер шрифта фильтров")]
            public int FontSize = 12;

            [JsonProperty("Цвет текста фильтров")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет активного фильтра")]
            public string ActiveColor = "0.25 0.69 1 0.85";

            [JsonProperty("Цвет неактивного фильтра")]
            public string InactiveColor = "0.22 0.23 0.25 0.85";
        }

        public class PlayerListSearchSettings
        {
            [JsonProperty("Высота панели поиска")]
            public int Height = 32;

            [JsonProperty("Ширина панели поиска")]
            public int Width = 660;

            [JsonProperty("Отступ поиска сверху (по Y)")]
            public int OffsetY = 175;

            [JsonProperty("Размер шрифта поиска")]
            public int FontSize = 13;

            [JsonProperty("Внутренний отступ поля ввода по X")]
            public int PaddingX = 10;

            [JsonProperty("Фон поля ввода поиска")]
            public string BackgroundColor = "0.10 0.10 0.11 0.90";

            [JsonProperty("Цвет текста в поле поиска")]
            public string TextColor = "1 1 1 1";

            // CHANGE: Цвет текста подсказки поиска игроков
            [JsonProperty("Цвет текста подсказки (плейсхолдера)")]
            public string PlaceholderColor = "0.60 0.62 0.65 0.60";
        }

        public class PlayerListCardSettings
        {
            [JsonProperty("Количество колонок игроков")]
            public int Columns = 4;

            [JsonProperty("Ширина карточки игрока")]
            public int Width = 158;

            [JsonProperty("Высота карточки игрока")]
            public int Height = 36;

            [JsonProperty("Отступ по горизонтали между карточками")]
            public int SpacingX = 8;

            [JsonProperty("Отступ по вертикали между карточками")]
            public int SpacingY = 6;

            [JsonProperty("Размер шрифта ника игрока")]
            public int FontSize = 12;

            [JsonProperty("Цвет текста ника игрока")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Фон активной/живой карточки игрока")]
            public string ActiveCardColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Фон неактивной карточки игрока")]
            public string InactiveCardColor = "0.22 0.23 0.25 0.85";

            [JsonProperty("Цвет полосы статуса: Онлайн")]
            public string StatusOnlineColor = "0.28 0.65 0.32 1";

            [JsonProperty("Цвет полосы статуса: Оффлайн")]
            public string StatusOfflineColor = "0.75 0.25 0.25 1";

            [JsonProperty("Цвет полосы статуса: Спящий")]
            public string StatusSleepingColor = "0.85 0.55 0.20 1";
        }

        public class PlayerListSettings
        {
            [JsonProperty("Фильтры игроков")]
            public PlayerListFilterSettings Filters = new PlayerListFilterSettings();

            [JsonProperty("Панель поиска")]
            public PlayerListSearchSettings Search = new PlayerListSearchSettings();

            [JsonProperty("Карточки игроков")]
            public PlayerListCardSettings Card = new PlayerListCardSettings();
        }

        public class UserInfoBackButtonSettings
        {
            [JsonProperty("Ширина кнопки Назад")]
            public int Width = 140;

            [JsonProperty("Высота кнопки Назад")]
            public int Height = 32;

            [JsonProperty("Влево/вправо кнопки Назад (OffsetX)")]
            public int OffsetX = -255;

            [JsonProperty("Вверх/вниз кнопки Назад (OffsetY)")]
            public int OffsetY = 222;

            [JsonProperty("Размер шрифта кнопки Назад")]
            public int FontSize = 13;

            [JsonProperty("Фон кнопки Назад")]
            public string BackgroundColor = "0.22 0.23 0.25 0.85";

            [JsonProperty("Цвет текста кнопки Назад")]
            public string TextColor = "1 1 1 1";
        }

        public class UserInfoAvatarSettings
        {
            [JsonProperty("Размер аватарки")]
            public int Size = 120;

            [JsonProperty("Отступ аватарки по X (OffsetX)")]
            public int OffsetX = -255;

            [JsonProperty("Отступ аватарки по Y (OffsetY)")]
            public int OffsetY = 138;

            [JsonProperty("Фон рамки аватарки")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";
        }

        public class UserInfoDetailsSettings
        {
            [JsonProperty("Ширина блока инфо")]
            public int Width = 500;

            [JsonProperty("Высота блока инфо")]
            public int Height = 120;

            [JsonProperty("Отступ блока инфо по X (OffsetX)")]
            public int OffsetX = 75;

            [JsonProperty("Отступ блока инфо по Y (OffsetY)")]
            public int OffsetY = 138;

            [JsonProperty("Фон блока инфо")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Размер шрифта ника")]
            public int NameFontSize = 18;

            [JsonProperty("Цвет текста ника")]
            public string NameColor = "0.25 0.69 1 1";

            [JsonProperty("Размер шрифта деталей")]
            public int DetailsFontSize = 13;

            [JsonProperty("Цвет текста деталей")]
            public string DetailsColor = "0.70 0.72 0.75 1";

            [JsonProperty("Отступ левой колонки деталей (OffsetMinX)")]
            public int LeftColumnOffsetMinX = 15;

            [JsonProperty("Отступ правой колонки деталей (OffsetMaxX)")]
            public int RightColumnOffsetMaxX = -15;
        }

        public class UserInfoActionButtonsSettings
        {
            [JsonProperty("Ширина кнопки действия")]
            public int Width = 158;

            [JsonProperty("Высота кнопки действия")]
            public int Height = 38;

            [JsonProperty("Начальный X кнопок действий (OffsetX)")]
            public int StartX = -249;

            [JsonProperty("Начальный Y кнопок действий (OffsetY)")]
            public int StartY = 50;

            [JsonProperty("Отступ по X между действиями")]
            public int SpacingX = 8;

            [JsonProperty("Отступ по Y между действиями")]
            public int SpacingY = 8;

            [JsonProperty("Размер шрифта кнопок действий")]
            public int FontSize = 12;

            [JsonProperty("Цвет текста кнопок действий")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет стандартной кнопки")]
            public string DefaultButtonColor = "0.22 0.23 0.25 0.85";

            [JsonProperty("Цвет активной кнопки")]
            public string ActiveButtonColor = "0.25 0.69 1 0.85";

            [JsonProperty("Цвет кнопки успеха (лечение)")]
            public string SuccessButtonColor = "0.28 0.65 0.32 0.85";

            // CHANGE: Настройка цвета фона кнопки снятия радиации
            [JsonProperty("Цвет кнопки снятия радиации")]
            public string RadiationButtonColor = "0.85 0.65 0.15 0.85";

            [JsonProperty("Цвет кнопки внимания (опасные/важные действия)")]
            public string WarningButtonColor = "0.85 0.55 0.20 0.85";
        }

        public class UserInfoSettings
        {
            [JsonProperty("Кнопка Назад")]
            public UserInfoBackButtonSettings BackButton = new UserInfoBackButtonSettings();

            [JsonProperty("Аватарка игрока")]
            public UserInfoAvatarSettings Avatar = new UserInfoAvatarSettings();

            [JsonProperty("Блок информации")]
            public UserInfoDetailsSettings Details = new UserInfoDetailsSettings();

            [JsonProperty("Кнопки действий")]
            public UserInfoActionButtonsSettings Actions = new UserInfoActionButtonsSettings();
        }

        public class PermTabsSettings
        {
            [JsonProperty("Ширина переключателя режима")]
            public int Width = 130;

            [JsonProperty("Высота переключателей режимов")]
            public int Height = 32;

            [JsonProperty("Отступ переключателей режимов Вверх/Вниз (OffsetY)")]
            public int OffsetY = 215;

            [JsonProperty("Размер шрифта переключателей")]
            public int FontSize = 12;

            [JsonProperty("Цвет текста переключателей")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет активного таба")]
            public string ActiveColor = "0.25 0.69 1 0.85";

            [JsonProperty("Цвет неактивного таба")]
            public string InactiveColor = "0.22 0.23 0.25 0.85";
        }

        public class PermCreateGroupBtnSettings
        {
            [JsonProperty("Ширина кнопки создания группы")]
            public int Width = 140;

            [JsonProperty("Высота кнопки создания группы")]
            public int Height = 32;

            [JsonProperty("Размер шрифта")]
            public int FontSize = 12;

            [JsonProperty("Фон кнопки")]
            public string BackgroundColor = "0.28 0.65 0.32 0.85";

            [JsonProperty("Цвет текста")]
            public string TextColor = "1 1 1 1";
        }

        public class PermGroupsBarSettings
        {
            // CHANGE: Добавлено свойство ScrollWidth для настройки ширины области скролла панели групп
            [JsonProperty("Ширина области скролла групп")]
            public int ScrollWidth = 720;

            [JsonProperty("Ширина кнопки группы")]
            public int Width = 110;

            [JsonProperty("Высота кнопки группы")]
            public int Height = 30;

            [JsonProperty("Отступ строки групп Вверх/Вниз (OffsetY)")]
            public int OffsetY = 165;

            [JsonProperty("Отступ между кнопками групп")]
            public int Spacing = 6;

            [JsonProperty("Размер шрифта кнопок групп")]
            public int FontSize = 12;

            [JsonProperty("Цвет текста кнопок групп")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет активной группы")]
            public string ActiveGroupColor = "0.25 0.69 1 0.85";

            [JsonProperty("Цвет неактивной группы")]
            public string InactiveGroupColor = "0.22 0.23 0.25 0.85";
        }

        public class PermSearchSettings
        {
            [JsonProperty("Ширина поля поиска")]
            public int Width = 340;

            [JsonProperty("Высота поля поиска")]
            public int Height = 30;

            [JsonProperty("Отступ поля поиска Влево/Вправо (OffsetX)")]
            public int OffsetX = -140;

            [JsonProperty("Отступ поля поиска Вверх/Вниз (OffsetY)")]
            public int OffsetY = 115;

            [JsonProperty("Размер шрифта поля поиска")]
            public int FontSize = 12;

            [JsonProperty("Внутренний отступ поля ввода по X")]
            public int PaddingX = 10;

            [JsonProperty("Фон поля поиска")]
            public string BackgroundColor = "0.10 0.10 0.11 0.90";

            [JsonProperty("Цвет текста поля поиска")]
            public string TextColor = "1 1 1 1";

            // CHANGE: Цвет текста подсказки поиска прав
            [JsonProperty("Цвет текста подсказки (плейсхолдера)")]
            public string PlaceholderColor = "0.60 0.62 0.65 0.60";
        }

        public class PermGroupActionButtonsSettings
        {
            [JsonProperty("Ширина кнопки действия над группой")]
            public int Width = 140;

            [JsonProperty("Высота кнопки действия над группой")]
            public int Height = 30;

            [JsonProperty("Отступ кнопки клонирования Влево/Вправо (OffsetX)")]
            public int CloneOffsetX = 110;

            [JsonProperty("Отступ кнопки удаления Влево/Вправо (OffsetX)")]
            public int DeleteOffsetX = 260;

            [JsonProperty("Отступ кнопок действий Вверх/Вниз (OffsetY)")]
            public int OffsetY = 115;

            [JsonProperty("Размер шрифта кнопок")]
            public int FontSize = 12;

            [JsonProperty("Цвет кнопки клонирования")]
            public string CloneButtonColor = "0.22 0.23 0.25 0.85";

            [JsonProperty("Цвет кнопки удаления")]
            public string DeleteButtonColor = "0.75 0.25 0.25 0.85";

            [JsonProperty("Цвет текста кнопок")]
            public string TextColor = "1 1 1 1";
        }

        public class PermBulkActionButtonsSettings
        {
            [JsonProperty("Ширина кнопки Назад к плагинам")]
            public int BackWidth = 160;

            [JsonProperty("Высота кнопки Назад к плагинам")]
            public int BackHeight = 30;

            [JsonProperty("Отступ кнопки Назад Влево/Вправо (OffsetX)")]
            public int BackOffsetX = -240;

            [JsonProperty("Ширина кнопки Выдать/Снять все")]
            public int ActionWidth = 105;

            [JsonProperty("Высота кнопки Выдать/Снять все")]
            public int ActionHeight = 30;

            [JsonProperty("Отступ кнопки Выдать все Влево/Вправо (OffsetX)")]
            public int GrantAllOffsetX = 155;

            [JsonProperty("Отступ кнопки Снять все Влево/Вправо (OffsetX)")]
            public int RevokeAllOffsetX = 265;

            [JsonProperty("Отступ кнопок действий Вверх/Вниз (OffsetY)")]
            public int OffsetY = 115;

            [JsonProperty("Ширина заголовка плагина в строке действий")]
            public int PluginTitleWidth = 240;

            [JsonProperty("Отступ заголовка плагина Влево/Вправо (OffsetX)")]
            public int PluginTitleOffsetX = -30;

            [JsonProperty("Размер шрифта кнопок")]
            public int FontSize = 12;

            [JsonProperty("Цвет кнопки Назад")]
            public string BackButtonColor = "0.22 0.23 0.25 0.85";

            [JsonProperty("Цвет кнопки Выдать все")]
            public string GrantAllColor = "0.28 0.65 0.32 0.85";

            [JsonProperty("Цвет кнопки Снять все")]
            public string RevokeAllColor = "0.75 0.25 0.25 0.85";

            [JsonProperty("Цвет текста кнопок")]
            public string TextColor = "1 1 1 1";
        }

        public class PermPluginGridSettings
        {
            [JsonProperty("Ширина карточки плагина")]
            public int CardWidth = 210;

            [JsonProperty("Высота карточки плагина")]
            public int CardHeight = 42;

            [JsonProperty("Количество колонок плагинов")]
            public int Columns = 3;

            [JsonProperty("Отступ между плагинами по X")]
            public int SpacingX = 10;

            [JsonProperty("Отступ между плагинами по Y")]
            public int SpacingY = 8;

            [JsonProperty("Размер шрифта названия плагина")]
            public int FontSize = 12;

            [JsonProperty("Фон карточки плагина")]
            public string CardBackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Цвет текста названия плагина")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Цвет счетчика прав")]
            public string CountColor = "0.25 0.69 1 1";
        }

        public class PermPermissionGridSettings
        {
            [JsonProperty("Ширина карточки права")]
            public int CardWidth = 320;

            [JsonProperty("Высота карточки права")]
            public int CardHeight = 36;

            [JsonProperty("Количество колонок прав")]
            public int Columns = 2;

            [JsonProperty("Отступ между правами по X")]
            public int SpacingX = 10;

            [JsonProperty("Отступ между правами по Y")]
            public int SpacingY = 6;

            [JsonProperty("Размер шрифта права")]
            public int FontSize = 12;

            [JsonProperty("Цвет фона выданного права")]
            public string GrantedColor = "0.28 0.65 0.32 0.85";

            [JsonProperty("Цвет фона невыданного права")]
            public string RevokedColor = "0.22 0.23 0.25 0.85";

            [JsonProperty("Цвет текста названия права")]
            public string TextColor = "1 1 1 1";
        }

        public class PermUserModeSettings
        {
            [JsonProperty("Ширина заголовка пользователя")]
            public int UserTitleWidth = 470;

            [JsonProperty("Отступ заголовка пользователя Влево/Вправо (OffsetX)")]
            public int UserTitleOffsetX = 85;

            [JsonProperty("Ширина строки статуса прав пользователя")]
            public int UserPermTitleWidth = 250;

            [JsonProperty("Отступ строки статуса прав пользователя Влево/Вправо (OffsetX)")]
            public int UserPermTitleOffsetX = 195;

            [JsonProperty("Отступ поиска пользователей Влево/Вправо (OffsetX)")]
            public int UserSearchOffsetX = -100;

            [JsonProperty("Ширина счетчика пользователей")]
            public int UserTotalLabelWidth = 230;

            [JsonProperty("Отступ счетчика пользователей Влево/Вправо (OffsetX)")]
            public int UserTotalLabelOffsetX = 205;

            [JsonProperty("Отступ заголовка игрока Вверх/Вниз (OffsetY)")]
            public int UserTitleOffsetY = 165;

            [JsonProperty("Отступ групп игрока Вверх/Вниз (OffsetY)")]
            public int UserGroupsOffsetY = 125;

            [JsonProperty("Отступ строки действий игрока Вверх/Вниз (OffsetY)")]
            public int UserActionRowOffsetY = 75;

            [JsonProperty("Ширина карточки игрока в списке")]
            public int UserPlayerCardWidth = 210;

            [JsonProperty("Высота карточки игрока в списке")]
            public int UserPlayerCardHeight = 44;

            [JsonProperty("Размер шрифта заголовка")]
            public int HeaderFontSize = 14;

            [JsonProperty("Размер шрифта элементов")]
            public int ItemFontSize = 12;

            [JsonProperty("Цвет заголовков игрока")]
            public string HeaderColor = "0.25 0.69 1 1";

            [JsonProperty("Цвет вторичного текста (группы, счетчики)")]
            public string DetailsColor = "0.70 0.72 0.75 1";

            [JsonProperty("Фон карточки игрока")]
            public string CardBackgroundColor = "0.20 0.21 0.23 0.75";
        }

        public class PermissionManagerSettings
        {
            [JsonProperty("Переключатели режимов (Группы / Игроки)")]
            public PermTabsSettings Tabs = new PermTabsSettings();

            [JsonProperty("Кнопка создания группы")]
            public PermCreateGroupBtnSettings CreateGroupButton = new PermCreateGroupBtnSettings();

            [JsonProperty("Панель групп")]
            public PermGroupsBarSettings GroupsBar = new PermGroupsBarSettings();

            [JsonProperty("Поле поиска")]
            public PermSearchSettings Search = new PermSearchSettings();

            [JsonProperty("Кнопки действий над группой")]
            public PermGroupActionButtonsSettings GroupActions =
                new PermGroupActionButtonsSettings();

            [JsonProperty("Кнопки управления правами")]
            public PermBulkActionButtonsSettings BulkActions = new PermBulkActionButtonsSettings();

            [JsonProperty("Сетка карточек плагинов")]
            public PermPluginGridSettings PluginGrid = new PermPluginGridSettings();

            [JsonProperty("Сетка карточек прав")]
            public PermPermissionGridSettings PermGrid = new PermPermissionGridSettings();

            [JsonProperty("Режим пользователей")]
            public PermUserModeSettings UserMode = new PermUserModeSettings();
        }

        public class PluginManagerSearchSettings
        {
            [JsonProperty("Высота поля поиска")]
            public int Height = 32;

            [JsonProperty("Ширина поля поиска")]
            public int Width = 500;

            [JsonProperty("Отступ поиска по X (OffsetX)")]
            public int OffsetX = -80;

            [JsonProperty("Отступ поиска по Y (OffsetY)")]
            public int OffsetY = 215;

            [JsonProperty("Размер шрифта поля поиска")]
            public int FontSize = 13;

            [JsonProperty("Внутренний отступ поля ввода по X")]
            public int PaddingX = 10;

            [JsonProperty("Фон поля поиска")]
            public string BackgroundColor = "0.10 0.10 0.11 0.90";

            [JsonProperty("Цвет текста поля поиска")]
            public string TextColor = "1 1 1 1";

            // CHANGE: Цвет текста подсказки поиска плагинов
            [JsonProperty("Цвет текста подсказки (плейсхолдера)")]
            public string PlaceholderColor = "0.60 0.62 0.65 0.60";
        }

        public class PluginManagerReloadAllSettings
        {
            [JsonProperty("Ширина кнопки Перезагрузить всё")]
            public int Width = 150;

            [JsonProperty("Высота кнопки Перезагрузить всё")]
            public int Height = 32;

            [JsonProperty("Отступ кнопки Перезагрузить всё по X (OffsetX)")]
            public int OffsetX = 255;

            [JsonProperty("Отступ кнопки Перезагрузить всё по Y (OffsetY)")]
            public int OffsetY = 215;

            [JsonProperty("Размер шрифта")]
            public int FontSize = 11;

            [JsonProperty("Фон кнопки Перезагрузить всё")]
            public string BackgroundColor = "0.85 0.55 0.20 0.85";

            [JsonProperty("Цвет текста")]
            public string TextColor = "1 1 1 1";
        }

        public class PluginManagerRowSettings
        {
            [JsonProperty("Ширина строки плагина")]
            public int Width = 660;

            [JsonProperty("Высота строки плагина")]
            public int Height = 42;

            [JsonProperty("Отступ между строками плагинов")]
            public int Spacing = 6;

            [JsonProperty("Размер шрифта названия")]
            public int TitleFontSize = 13;

            [JsonProperty("Размер шрифта описания")]
            public int DescFontSize = 11;

            [JsonProperty("Фон строки плагина")]
            public string BackgroundColor = "0.20 0.21 0.23 0.75";

            [JsonProperty("Цвет текста названия")]
            public string TitleColor = "1 1 1 1";

            [JsonProperty("Цвет текста описания")]
            public string DescColor = "0.70 0.72 0.75 1";
        }

        public class PluginManagerStatusBadgeSettings
        {
            [JsonProperty("Ширина бейджа статуса")]
            public int Width = 85;

            [JsonProperty("Высота бейджа статуса")]
            public int Height = 26;

            [JsonProperty("Размер шрифта статуса")]
            public int FontSize = 11;

            [JsonProperty("Цвет бейджа: Загружен")]
            public string LoadedColor = "0.28 0.65 0.32 1";

            [JsonProperty("Цвет бейджа: Выгружен")]
            public string UnloadedColor = "0.75 0.25 0.25 1";

            [JsonProperty("Цвет текста статуса")]
            public string TextColor = "1 1 1 1";
        }

        public class PluginManagerActionButtonsSettings
        {
            [JsonProperty("Ширина кнопок действий")]
            public int Width = 85;

            [JsonProperty("Высота кнопок действий")]
            public int Height = 28;

            [JsonProperty("Отступ между кнопками действий")]
            public int Spacing = 6;

            [JsonProperty("Размер шрифта кнопок")]
            public int FontSize = 11;

            [JsonProperty("Цвет кнопки Перезагрузить")]
            public string ReloadColor = "0.22 0.23 0.25 0.85";

            [JsonProperty("Цвет кнопки Выгрузить")]
            public string UnloadColor = "0.75 0.25 0.25 0.85";

            [JsonProperty("Цвет кнопки Загрузить")]
            public string LoadColor = "0.28 0.65 0.32 0.85";

            [JsonProperty("Цвет текста кнопок")]
            public string TextColor = "1 1 1 1";
        }

        public class PluginManagerSettings
        {
            [JsonProperty("Панель поиска")]
            public PluginManagerSearchSettings Search = new PluginManagerSearchSettings();

            [JsonProperty("Кнопка Перезагрузить всё")]
            public PluginManagerReloadAllSettings ReloadAllButton =
                new PluginManagerReloadAllSettings();

            [JsonProperty("Строка плагина")]
            public PluginManagerRowSettings Row = new PluginManagerRowSettings();

            [JsonProperty("Бейдж статуса")]
            public PluginManagerStatusBadgeSettings StatusBadge =
                new PluginManagerStatusBadgeSettings();

            [JsonProperty("Кнопки действий")]
            public PluginManagerActionButtonsSettings ActionButtons =
                new PluginManagerActionButtonsSettings();
        }

        public class ModalTitleSettings
        {
            [JsonProperty("Размер шрифта заголовка")]
            public int FontSize = 16;

            [JsonProperty("Цвет текста заголовка")]
            public string TextColor = "0.25 0.69 1 1";

            [JsonProperty("Внутренний отступ по X")]
            public int PaddingX = 10;

            [JsonProperty("Отступ от верха (OffsetY)")]
            public int OffsetY = -30;

            [JsonProperty("Высота блока заголовка")]
            public int Height = 40;
        }

        public class ModalInputSettings
        {
            [JsonProperty("Ширина поля ввода")]
            public int Width = 380;

            [JsonProperty("Высота поля ввода")]
            public int Height = 34;

            [JsonProperty("Размер шрифта поля ввода")]
            public int FontSize = 13;

            [JsonProperty("Внутренний отступ по X")]
            public int PaddingX = 10;

            [JsonProperty("Максимальная длина текста")]
            public int CharsLimit = 64;

            [JsonProperty("Фон поля ввода")]
            public string BackgroundColor = "0.10 0.10 0.11 0.90";

            [JsonProperty("Цвет текста в поле ввода")]
            public string TextColor = "1 1 1 1";

            // CHANGE: Настройка цвета текста плейсхолдера модального окна
            [JsonProperty("Цвет текста подсказки (плейсхолдера)")]
            public string PlaceholderColor = "0.60 0.62 0.65 0.60";

            [JsonProperty("Цвет текста описания")]
            public string DescriptionTextColor = "0.70 0.72 0.75 1";
        }

        public class ModalButtonsSettings
        {
            [JsonProperty("Ширина кнопки")]
            public int Width = 130;

            [JsonProperty("Высота кнопки")]
            public int Height = 34;

            [JsonProperty("Размер шрифта")]
            public int FontSize = 13;

            [JsonProperty("Отступ снизу (OffsetY)")]
            public int OffsetY = 20;

            [JsonProperty("Отступ между кнопками (SpacingX)")]
            public int SpacingX = 10;

            [JsonProperty("Цвет кнопки подтверждения")]
            public string ConfirmColor = "0.28 0.65 0.32 0.85";

            [JsonProperty("Цвет кнопки отмены")]
            public string CancelColor = "0.75 0.25 0.25 0.85";

            [JsonProperty("Цвет текста кнопок")]
            public string TextColor = "1 1 1 1";
        }

        public class ModalSettings
        {
            [JsonProperty("Позиция модального окна")]
            public PanelSettings Panel = new PanelSettings
            {
                Width = 440,
                Height = 220,
                OffsetX = 0,
                OffsetY = 0,
            };

            [JsonProperty("Фон затемнения экрана")]
            public string OverlayColor = "0 0 0 0.85";

            [JsonProperty("Фон модального окна")]
            public string BackgroundColor = "0.13 0.14 0.15 0.98";

            [JsonProperty("Цвет рамки модального окна")]
            public string BorderColor = "0.25 0.69 1 0.50";

            [JsonProperty("Толщина рамки")]
            public int BorderSize = 1;

            [JsonProperty("Заголовок окна")]
            public ModalTitleSettings Title = new ModalTitleSettings();

            [JsonProperty("Поле ввода")]
            public ModalInputSettings Input = new ModalInputSettings();

            [JsonProperty("Кнопки управления")]
            public ModalButtonsSettings Buttons = new ModalButtonsSettings();
        }

        public enum ButtonHook
        {
            OFF,
            X,
            F,
        }

        public class Configuration
        {
            [JsonProperty("Общие настройки")]
            public GeneralSettings General = new GeneralSettings();

            // CHANGE: Секция креатив-режима — управление нативными конварами Creative.*
            [JsonProperty("Настройки креатив-режима")]
            public CreativeSettings Creative = new CreativeSettings();

            [JsonProperty("Панель навигации (левая панель)")]
            public NavigationSettings Navigation = new NavigationSettings();

            [JsonProperty("Верхняя панель (Заголовок)")]
            public HeaderSettings Header = new HeaderSettings();

            [JsonProperty("Панель контента")]
            public ContentSettings Content = new ContentSettings();

            [JsonProperty("Страница: Быстрое меню")]
            public QuickMenuSettings QuickMenu = new QuickMenuSettings();

            [JsonProperty("Страница: Список игроков")]
            public PlayerListSettings PlayerList = new PlayerListSettings();

            [JsonProperty("Страница: Информация об игроке (UserInfo)")]
            public UserInfoSettings UserInfo = new UserInfoSettings();

            [JsonProperty("Страница: Менеджер прав")]
            public PermissionManagerSettings PermissionManager = new PermissionManagerSettings();

            [JsonProperty("Страница: Менеджер плагинов")]
            public PluginManagerSettings PluginManager = new PluginManagerSettings();

            // CHANGE: Конфигурация детального раздела управления погодой
            [JsonProperty("Страница: Управление погодой")]
            public WeatherManagerSettings WeatherManager = new WeatherManagerSettings();

            [JsonProperty("Модальные окна")]
            public ModalSettings Modal = new ModalSettings();
        }

        private Configuration _config;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                    LoadDefaultConfig();
            }
            catch (Exception ex)
            {
                Puts(
                    $"Ошибка загрузки конфигурации RAdminMenu: {ex.Message}. Будет загружен дефолтный конфиг."
                );
                LoadDefaultConfig();
            }

            // CHANGE: Валидация и инициализация всех подсекций для плавной миграции конфига
            if (_config.General == null)
                _config.General = new GeneralSettings();
            if (_config.Creative == null)
                _config.Creative = new CreativeSettings();
            if (_config.Navigation == null)
                _config.Navigation = new NavigationSettings();
            if (_config.Header == null)
                _config.Header = new HeaderSettings();
            if (_config.Content == null)
                _config.Content = new ContentSettings();
            if (_config.QuickMenu == null)
                _config.QuickMenu = new QuickMenuSettings();
            if (_config.QuickMenu.TeleportCard == null)
                _config.QuickMenu.TeleportCard = new QuickMenuTeleportCardSettings();
            if (_config.QuickMenu.ActionsCard == null)
                _config.QuickMenu.ActionsCard = new QuickMenuActionsCardSettings();
            if (_config.QuickMenu.EventsCard == null)
                _config.QuickMenu.EventsCard = new QuickMenuEventsCardSettings();
            if (_config.QuickMenu.TimeCard == null)
                _config.QuickMenu.TimeCard = new QuickMenuTimeCardSettings();
            if (_config.QuickMenu.WeatherCard == null)
                _config.QuickMenu.WeatherCard = new QuickMenuWeatherCardSettings();
            if (_config.QuickMenu.WeatherCard.Panel == null)
                _config.QuickMenu.WeatherCard.Panel = new PanelSettings { Width = 660, Height = 100, OffsetX = 0, OffsetY = -280 };
            if (_config.QuickMenu.WeatherCard.DetailButton == null)
                _config.QuickMenu.WeatherCard.DetailButton = new QuickMenuWeatherDetailButtonSettings();
            if (_config.QuickMenu.WeatherCard.Buttons == null)
                _config.QuickMenu.WeatherCard.Buttons = new QuickMenuWeatherButtonsSettings();
            if (_config.WeatherManager == null)
                _config.WeatherManager = new WeatherManagerSettings();
            if (_config.WeatherManager.Tabs == null)
                _config.WeatherManager.Tabs = new WeatherTabsSettings();
            if (_config.WeatherManager.PresetsGrid == null)
                _config.WeatherManager.PresetsGrid = new WeatherPresetsGridSettings();
            if (_config.WeatherManager.Row == null)
                _config.WeatherManager.Row = new WeatherRowSettings();
            if (_config.WeatherManager.QuickButtons == null)
                _config.WeatherManager.QuickButtons = new WeatherQuickButtonsSettings();
            if (_config.WeatherManager.Input == null)
                _config.WeatherManager.Input = new WeatherInputBlockSettings();
            if (_config.WeatherManager.ApplyButton == null)
                _config.WeatherManager.ApplyButton = new WeatherApplyButtonSettings();
            if (_config.PlayerList == null)
                _config.PlayerList = new PlayerListSettings();
            if (_config.UserInfo == null)
                _config.UserInfo = new UserInfoSettings();
            if (_config.PermissionManager == null)
                _config.PermissionManager = new PermissionManagerSettings();
            if (_config.PermissionManager.PluginGrid == null)
                _config.PermissionManager.PluginGrid = new PermPluginGridSettings();
            if (_config.PermissionManager.PermGrid == null)
                _config.PermissionManager.PermGrid = new PermPermissionGridSettings();
            if (_config.PermissionManager.UserMode == null)
                _config.PermissionManager.UserMode = new PermUserModeSettings();
            if (_config.PluginManager == null)
                _config.PluginManager = new PluginManagerSettings();
            if (_config.Modal == null)
                _config.Modal = new ModalSettings();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    // Navigation & Header
                    ["NAV_HEADER_TITLE"] = "ADMIN MENU",
                    ["NAV_QUICK"] = "QUICK MENU",
                    ["NAV_PLAYERS"] = "PLAYERS",
                    ["NAV_PERMISSIONS"] = "PERMISSIONS",
                    ["NAV_PLUGINS"] = "PLUGINS",
                    ["NAV_QUICKMENU"] = "QUICK MENU",
                    ["NAV_USERINFO"] = "PLAYER INFORMATION",
                    ["NAV_WEATHER"] = "WEATHER CONTROL",
                    ["HEADER_ONLINE"] =
                        "Online: <color=#42b0ff>{0}/{1}</color> | Sleepers: <color=#a0a0a0>{2}</color> | Server FPS: <color=#52e252>{3}</color>",

                    // Quick Menu Groups & Actions
                    ["QM_GROUP_TELEPORT"] = "TELEPORTATION",
                    ["QM_CARD_TP"] = "TELEPORTATION",
                    ["QM_TP_MARKER"] = "TELEPORT TO MARKER",
                    ["QM_TP_DEATH"] = "DEATH POINT",
                    ["QM_TP_SPAWN"] = "TO SPAWN",
                    ["QM_TP_MARKER_ENABLED"] =
                        "<color=#52e252>Teleport to map marker enabled! Right-click anywhere on the map.</color>",
                    ["QM_TP_MARKER_DISABLED"] =
                        "<color=#e25252>Teleport to map marker disabled.</color>",

                    ["QM_GROUP_ACTIONS"] = "QUICK ACTIONS",
                    ["QM_CARD_ACTIONS"] = "QUICK ACTIONS",
                    ["QM_HEAL"] = "HEAL (100%)",
                    ["QM_ACT_HEAL"] = "HEAL (100%)",
                    ["QM_REPAIR"] = "REPAIR ITEMS",
                    ["QM_ACT_REPAIR"] = "REPAIR ITEMS",
                    ["QM_CLEAR_INV"] = "CLEAR INVENTORY",
                    // CHANGE: Кнопка креатив-режима в Быстром меню
                    ["QM_CREATIVE"] = "CREATIVE MODE",
                    ["QM_ACT_CLEAR_INV"] = "CLEAR INVENTORY",

                    ["QM_GROUP_EVENTS"] = "EVENT SPAWNER",
                    ["QM_CARD_EVENTS"] = "EVENT SPAWNER",
                    ["QM_HELI"] = "PATROL HELI",
                    ["QM_EVT_HELI"] = "PATROL HELI",
                    ["QM_BRADLEY"] = "BRADLEY APC",
                    ["QM_EVT_BRADLEY"] = "BRADLEY APC",
                    ["QM_CARGO"] = "CARGO SHIP",
                    ["QM_EVT_CARGO"] = "CARGO SHIP",
                    ["QM_AIRDROP"] = "AIRDROP PLANE",
                    ["QM_CHINOOK"] = "CHINOOK",

                    ["QM_GROUP_TIME"] = "TIME OF DAY",
                    ["QM_CARD_TIME"] = "TIME OF DAY",
                    ["QM_TIME_CURRENT"] = "Server Time: <color=#42b0ff>{0}</color>",
                    ["QM_TIME_DAY"] = "DAY (12:00)",
                    ["QM_TIME_NIGHT"] = "NIGHT (00:00)",
                    ["QM_TIME_APPLY"] = "APPLY",
                    ["QM_TIME_PLACEHOLDER"] = "0.0 - 24.0",

                    // Quick Menu Weather
                    ["QM_GROUP_WEATHER"] = "WEATHER CONTROL",
                    ["QM_CARD_WEATHER"] = "WEATHER CONTROL",
                    ["QM_WEATHER_CLEAR"] = "CLEAR",
                    ["QM_WEATHER_RAIN"] = "RAIN",
                    ["QM_WEATHER_FOG"] = "FOG",
                    ["QM_WEATHER_STORM"] = "STORM",
                    ["QM_WEATHER_RESET"] = "RESET",
                    ["QM_WEATHER_DETAILS"] = "⚙ SETTINGS",

                    // Weather Manager Page
                    ["WEATHER_TITLE"] = "WEATHER MANAGEMENT",
                    ["WEATHER_TAB_PRESETS"] = "PRESETS",
                    ["WEATHER_TAB_GAMEPLAY"] = "GAMEPLAY",
                    ["WEATHER_TAB_CHANCES"] = "CHANCES",
                    ["WEATHER_TAB_WEATHER"] = "WEATHER",
                    ["WEATHER_TAB_ATMOSPHERE"] = "ATMOSPHERE",
                    ["WEATHER_TAB_CLOUDS"] = "CLOUDS",

                    ["WEATHER_PRESET_CLEAR"] = "Clear Skies",
                    ["WEATHER_PRESET_CLEAR_DESC"] = "weather.load Clear",
                    ["WEATHER_PRESET_DUST"] = "Dust Storm",
                    ["WEATHER_PRESET_DUST_DESC"] = "weather.load Dust",
                    ["WEATHER_PRESET_FOG"] = "Dense Fog",
                    ["WEATHER_PRESET_FOG_DESC"] = "weather.load Fog",
                    ["WEATHER_PRESET_OVERCAST"] = "Overcast",
                    ["WEATHER_PRESET_OVERCAST_DESC"] = "weather.load Overcast",
                    ["WEATHER_PRESET_RAINHEAVY"] = "Heavy Rain",
                    ["WEATHER_PRESET_RAINHEAVY_DESC"] = "weather.load RainHeavy",
                    ["WEATHER_PRESET_RAINMILD"] = "Mild Rain",
                    ["WEATHER_PRESET_RAINMILD_DESC"] = "weather.load RainMild",
                    ["WEATHER_PRESET_STORM"] = "Thunderstorm",
                    ["WEATHER_PRESET_STORM_DESC"] = "weather.load Storm",
                    ["WEATHER_PRESET_RESET"] = "Reset to Dynamic",
                    ["WEATHER_PRESET_RESET_DESC"] = "weather.reset",

                    ["WEATHER_PARAM_AUTO"] = "Auto (-1)",
                    ["WEATHER_PARAM_APPLY"] = "APPLY",
                    ["WEATHER_PARAM_PLACEHOLDER"] = "-1.0 .. 1.0",
                    ["WEATHER_CHANCE_PLACEHOLDER"] = "0.0 .. 1.0",

                    ["PARAM_weather.wetness_rain"] = "Rain Wetness",
                    ["PARAM_weather.wetness_snow"] = "Snow Wetness",
                    ["PARAM_weather.clear_chance"] = "Clear Sky Chance",
                    ["PARAM_weather.dust_chance"] = "Dust Chance",
                    ["PARAM_weather.fog_chance"] = "Fog Chance",
                    ["PARAM_weather.overcast_chance"] = "Overcast Chance",
                    ["PARAM_weather.storm_chance"] = "Storm Chance",
                    ["PARAM_weather.rain_chance"] = "Rain Chance",
                    ["PARAM_weather.rain"] = "Rain Intensity",
                    ["PARAM_weather.wind"] = "Wind Speed",
                    ["PARAM_weather.thunder"] = "Thunder Frequency",
                    ["PARAM_weather.rainbow"] = "Rainbow Visibility",
                    ["PARAM_weather.fog"] = "Fog Density",
                    ["PARAM_weather.atmosphere_rayleigh"] = "Atmosphere Rayleigh",
                    ["PARAM_weather.atmosphere_mie"] = "Atmosphere Mie",
                    ["PARAM_weather.atmosphere_brightness"] = "Atmosphere Brightness",
                    ["PARAM_weather.atmosphere_contrast"] = "Atmosphere Contrast",
                    ["PARAM_weather.atmosphere_directionality"] = "Atmosphere Directionality",
                    ["PARAM_weather.cloud_size"] = "Cloud Size",
                    ["PARAM_weather.cloud_opacity"] = "Cloud Opacity",
                    ["PARAM_weather.cloud_coverage"] = "Cloud Coverage",
                    ["PARAM_weather.cloud_sharpness"] = "Cloud Sharpness",
                    ["PARAM_weather.cloud_coloring"] = "Cloud Coloring",
                    ["PARAM_weather.cloud_attenuation"] = "Cloud Attenuation",
                    ["PARAM_weather.cloud_scattering"] = "Cloud Scattering",
                    ["PARAM_weather.cloud_brightness"] = "Cloud Brightness",

                    ["WEATHER_MSG_SET"] = "<color=#52e252>Weather parameter</color> <color=#42b0ff>{0}</color> <color=#52e252>set to</color> <color=#ffea6c>{1}</color>",
                    ["WEATHER_MSG_LOAD"] = "<color=#52e252>Weather preset</color> <color=#42b0ff>{0}</color> <color=#52e252>loaded successfully!</color>",
                    ["WEATHER_MSG_RESET"] = "<color=#52e252>Weather reset to automatic dynamic cycle.</color>",
                    ["WEATHER_MSG_REPORT"] = "<color=#42b0ff>Weather Report:</color> Rain: {0:P0}, Wind: {1:P0}, Fog: {2:P0}, Clouds: {3:P0}",

                    // Player List Filters
                    ["FILTER_ONLINE"] = "ONLINE",
                    ["PL_FILTER_ONLINE"] = "ONLINE",
                    ["FILTER_OFFLINE"] = "OFFLINE",
                    ["PL_FILTER_OFFLINE"] = "OFFLINE",
                    ["FILTER_SLEEPING"] = "SLEEPING",
                    ["PL_FILTER_SLEEPING"] = "SLEEPING",
                    ["FILTER_ADMINS"] = "ADMINS",
                    ["PL_FILTER_ADMINS"] = "ADMINS",
                    ["FILTER_MODS"] = "MODERATORS",
                    ["PL_FILTER_MODS"] = "MODERATORS",
                    ["FILTER_ALL"] = "ALL",
                    ["PL_FILTER_ALL"] = "ALL",
                    ["PL_SEARCH_PLACEHOLDER"] = "Search by name or SteamID...",
                    ["PL_NO_PLAYERS"] = "No players found",

                    // User Info
                    ["UI_BACK"] = "◀ BACK TO LIST",
                    ["USER_BACK"] = "◀ BACK TO LIST",
                    ["UI_PING_IP"] = "IP: <color=#42b0ff>{0}</color> | Ping: <color=#52e252>{1}ms</color>",
                    ["UI_HEALTH"] = "Health: <color=#52e252>{0}/{1} HP</color>",
                    ["USER_HEALTH"] = "Health: <color=#52e252>{0}/{1} HP</color>",
                    ["UI_RADIATION"] = "Radiation: <color=#ffb03b>{0}</color>",
                    ["UI_GRID"] = "Grid: <color=#42b0ff>{0}</color>",
                    ["USER_LOCATION"] = "Grid: <color=#42b0ff>{0}</color>",
                    ["UI_BALANCE"] = "Balance: <color=#52e252>${0:N0}</color>",
                    ["UI_CLAN"] = "Clan: <color=#42b0ff>[{0}]</color>",
                    ["UI_CTIME"] = "Session: <color=#a0a0a0>{0}h {1}m {2}s</color>",
                    ["UI_STATUS_OFFLINE"] = "Status: <color=#e25252>Offline</color>",
                    ["USER_PING"] = "Ping",
                    ["USER_STEAM_DATE"] = "Registered",
                    ["USER_RUST_HOURS"] = "Rust Hours",

                    // User Actions
                    ["ACT_TP_SELF_TO"] = "TELEPORT TO",
                    ["USER_ACT_TP_TO"] = "TELEPORT TO",
                    ["ACT_TP_TO_SELF"] = "TELEPORT HERE",
                    ["USER_ACT_TP_HERE"] = "TELEPORT HERE",
                    ["ACT_TP_AUTH"] = "TELEPORT TO AUTH",
                    ["USER_ACT_TP_AUTH"] = "TELEPORT TO AUTH",
                    ["ACT_TP_DEATH"] = "TELEPORT TO DEATH",
                    ["USER_ACT_TP_DEATH"] = "TELEPORT TO DEATH",
                    ["ACT_HEAL_100"] = "HEAL (100%)",
                    ["USER_ACT_HEAL_100"] = "HEAL (100%)",
                    ["ACT_CLEAR_RAD"] = "CLEAR RAD",
                    ["USER_ACT_CLEAR_RAD"] = "CLEAR RAD",
                    ["ACT_HEAL_SUCCESS"] = "<color=#52e252>Healed {0} to 100%!</color>",
                    ["ACT_CLEAR_RAD_SUCCESS"] = "<color=#52e252>Radiation cleared for {0}!</color>",
                    ["ACT_SPECTATE"] = "SPECTATE",
                    ["USER_ACT_SPECTATE"] = "SPECTATE",
                    ["ACT_CREATIVE"] = "CREATIVE MODE",
                    ["USER_ACT_CREATIVE"] = "CREATIVE MODE",
                    ["ACT_CUFF"] = "HANDCUFFS",
                    ["USER_ACT_CUFF"] = "HANDCUFFS",
                    ["ACT_STRIP_INV"] = "STRIP INVENTORY",
                    ["USER_ACT_STRIP"] = "STRIP INVENTORY",
                    ["ACT_VIEW_INV"] = "VIEW INVENTORY",
                    ["USER_ACT_VIEW_INV"] = "VIEW INVENTORY",
                    ["ACT_VIEW_BP"] = "VIEW BACKPACK",
                    ["USER_ACT_VIEW_BP"] = "VIEW BACKPACK",
                    ["ACT_UNLOCK_BP"] = "UNLOCK BLUEPRINTS",
                    ["USER_ACT_UNLOCK_BP"] = "UNLOCK BLUEPRINTS",
                    ["ACT_REVOKE_BP"] = "REVOKE BLUEPRINTS",
                    ["ACT_REVOKE_BP_GRANTED"] = "REVOKE GRANTED BP",
                    ["USER_ACT_REVOKE_BP"] = "REVOKE BLUEPRINTS",
                    ["ACT_KILL"] = "KILL",
                    ["USER_ACT_KILL"] = "KILL",
                    ["ACT_CREATIVE_ENABLED"] =
                        "<color=#52e252>Creative mode enabled for {0}!</color>",
                    ["ACT_CREATIVE_DISABLED"] =
                        "<color=#e25252>Creative mode disabled for {0}!</color>",
                    ["ACT_CREATIVE_TARGET_ON"] =
                        "<color=#52e252>You have been granted creative mode!</color>",
                    ["ACT_CREATIVE_TARGET_OFF"] =
                        "<color=#e25252>Your creative mode has been disabled.</color>",
                    ["ERR_NO_BACKPACK"] =
                        "<color=#e25252>Player {0} does not have a backpack equipped!</color>",

                    // Permissions
                    ["PERM_GROUPS"] = "GROUPS",
                    ["PERM_USERS"] = "USERS",
                    ["PERM_CREATE_GROUP"] = "+ CREATE GROUP",
                    ["PERM_CLONE_GROUP"] = "CLONE GROUP",
                    ["PERM_REMOVE_GROUP"] = "DELETE GROUP",
                    ["PERM_BACK_TO_PLUGINS"] = "◀ BACK TO PLUGINS",
                    ["PERM_BACK_TO_USERS"] = "◀ BACK TO USERS",
                    ["PERM_GRANT_ALL"] = "GRANT ALL",
                    ["PERM_REVOKE_ALL"] = "REVOKE ALL",
                    ["PERM_TOTAL_USERS"] = "Total users: <color=#42b0ff>{0}</color>",
                    ["PERM_USER_GROUPS_TITLE"] = "Groups of {0}:",
                    ["PERM_USER_PERMS_TITLE"] = "Direct permissions: <color=#52e252>{0}</color>",
                    ["PERM_DIRECT"] = "DIRECT",
                    ["PERM_GROUP"] = "GROUP",
                    ["PERM_SEARCH_PLACEHOLDER"] = "Search plugins...",
                    ["PERM_USER_SEARCH_PLACEHOLDER"] = "Search players...",

                    // Plugins
                    ["PLUGINS_RELOAD_ALL"] = "↻ RELOAD ALL",
                    ["PLUGINS_LOAD"] = "LOAD",
                    ["PLUGINS_UNLOAD"] = "UNLOAD",
                    ["PLUGINS_RELOAD"] = "RELOAD",
                    ["STATUS_LOADED"] = "LOADED",
                    ["STATUS_UNLOADED"] = "UNLOADED",
                    ["PM_SEARCH_PLACEHOLDER"] = "Search plugins...",

                    // Modals
                    ["MODAL_CONFIRM"] = "CONFIRM",
                    ["MODAL_CANCEL"] = "CANCEL",
                    ["MODAL_GROUP_NAME"] = "Enter group name...",
                    ["MODAL_PLACEHOLDER_DEFAULT"] = "Enter value...",
                    ["MODAL_CLONE_GROUP_TITLE"] = "Clone group: {0}",
                    ["MODAL_DELETE_GROUP_TITLE"] = "Delete group: {0}?",
                    ["MODAL_DELETE_GROUP_DESC"] =
                        "Are you sure you want to delete group <color=#e25252>{0}</color>? This action cannot be undone.",
                    // CHANGE: Тексты подтверждения опасных действий над игроком
                    ["MODAL_CONFIRM_ACTION_TITLE"] = "ACTION CONFIRMATION",
                    ["MODAL_CONFIRM_KILL_DESC"] = "Kill player <color=#e25252>{0}</color>?",
                    ["MODAL_CONFIRM_STRIP_DESC"] =
                        "Clear inventory of player <color=#e25252>{0}</color>?",
                    ["MODAL_CONFIRM_REVOKE_DESC"] =
                        "Fully reset blueprints of player <color=#e25252>{0}</color>? Only default blueprints will remain.",
                    ["MODAL_CONFIRM_REVOKE_GRANTED_DESC"] =
                        "Revoke plugin-granted blueprints of player <color=#e25252>{0}</color>?",
                    ["ACT_REVOKE_GRANTED_NO_SNAPSHOT"] =
                        "<color=#e25252>Nothing to revoke:</color> the \"UNLOCK BLUEPRINTS\" button from this menu has not been used for this player yet, so the granted blueprints list was not saved. Press \"UNLOCK BLUEPRINTS\" first — then \"REVOKE GRANTED BP\" will work. Alternatively press \"REVOKE BLUEPRINTS\" for a full reset.",
                },
                this
            );

            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    // Navigation & Header
                    ["NAV_HEADER_TITLE"] = "ADMIN MENU",
                    ["NAV_QUICK"] = "БЫСТРОЕ МЕНЮ",
                    ["NAV_PLAYERS"] = "ИГРОКИ",
                    ["NAV_PERMISSIONS"] = "ПРАВА И ГРУППЫ",
                    ["NAV_PLUGINS"] = "ПЛАГИНЫ",
                    ["NAV_QUICKMENU"] = "БЫСТРОЕ МЕНЮ",
                    ["NAV_USERINFO"] = "ИНФОРМАЦИЯ ОБ ИГРОКЕ",
                    ["NAV_WEATHER"] = "УПРАВЛЕНИЕ ПОГОДОЙ",
                    ["HEADER_ONLINE"] =
                        "Онлайн: <color=#42b0ff>{0}/{1}</color> | Спящих: <color=#a0a0a0>{2}</color> | FPS Сервера: <color=#52e252>{3}</color>",

                    // Quick Menu Groups & Actions
                    ["QM_GROUP_TELEPORT"] = "ТЕЛЕПОРТАЦИЯ",
                    ["QM_CARD_TP"] = "ТЕЛЕПОРТАЦИЯ",
                    ["QM_TP_MARKER"] = "ТП ПО МАРКЕРУ",
                    ["QM_TP_DEATH"] = "ТОЧКА СМЕРТИ",
                    ["QM_TP_SPAWN"] = "НА СПАВН",
                    ["QM_TP_MARKER_ENABLED"] =
                        "<color=#52e252>Телепортация по маркеру включена! Поставьте маркер ПКМ на карте.</color>",
                    ["QM_TP_MARKER_DISABLED"] =
                        "<color=#e25252>Телепортация по маркеру отключена.</color>",

                    ["QM_GROUP_ACTIONS"] = "БЫСТРЫЕ ДЕЙСТВИЯ",
                    ["QM_CARD_ACTIONS"] = "БЫСТРЫЕ ДЕЙСТВИЯ",
                    ["QM_HEAL"] = "ЛЕЧЕНИЕ (100%)",
                    ["QM_ACT_HEAL"] = "ЛЕЧЕНИЕ (100%)",
                    ["QM_REPAIR"] = "ПОЧИНКА ПРЕДМЕТОВ",
                    ["QM_ACT_REPAIR"] = "ПОЧИНКА ПРЕДМЕТОВ",
                    ["QM_CLEAR_INV"] = "ОЧИСТИТЬ ИНВЕНТАРЬ",
                    // CHANGE: Кнопка креатив-режима в Быстром меню
                    ["QM_CREATIVE"] = "КРЕАТИВ-РЕЖИМ",
                    ["QM_ACT_CLEAR_INV"] = "ОЧИСТИТЬ ИНВЕНТАРЬ",

                    ["QM_GROUP_EVENTS"] = "ВЫЗОВ ИВЕНТОВ",
                    ["QM_CARD_EVENTS"] = "ВЫЗОВ ИВЕНТОВ",
                    ["QM_HELI"] = "ВЕРТОЛЕТ",
                    ["QM_EVT_HELI"] = "ВЕРТОЛЕТ",
                    ["QM_BRADLEY"] = "ТАНК BRADLEY",
                    ["QM_EVT_BRADLEY"] = "ТАНК BRADLEY",
                    ["QM_CARGO"] = "КОРАБЛЬ CARGO",
                    ["QM_EVT_CARGO"] = "КОРАБЛЬ CARGO",
                    ["QM_AIRDROP"] = "САМОЛЁТ",
                    ["QM_CHINOOK"] = "ЧИНУК",

                    ["QM_GROUP_TIME"] = "ВРЕМЯ СУТОК",
                    ["QM_CARD_TIME"] = "ВРЕМЯ СУТОК",
                    ["QM_TIME_CURRENT"] = "Время на сервере: <color=#42b0ff>{0}</color>",
                    ["QM_TIME_DAY"] = "ДЕНЬ (12:00)",
                    ["QM_TIME_NIGHT"] = "НОЧЬ (00:00)",
                    ["QM_TIME_APPLY"] = "ПРИМЕНИТЬ",
                    ["QM_TIME_PLACEHOLDER"] = "0.0 - 24.0",

                    // Quick Menu Weather
                    ["QM_GROUP_WEATHER"] = "УПРАВЛЕНИЕ ПОГОДОЙ",
                    ["QM_CARD_WEATHER"] = "УПРАВЛЕНИЕ ПОГОДОЙ",
                    ["QM_WEATHER_CLEAR"] = "ЯСНО",
                    ["QM_WEATHER_RAIN"] = "ДОЖДЬ",
                    ["QM_WEATHER_FOG"] = "ТУМАН",
                    ["QM_WEATHER_STORM"] = "ГРОЗА",
                    ["QM_WEATHER_RESET"] = "СБРОС",
                    ["QM_WEATHER_DETAILS"] = "⚙ НАСТРОЙКИ",

                    // Weather Manager Page
                    ["WEATHER_TITLE"] = "УПРАВЛЕНИЕ ПОГОДОЙ",
                    ["WEATHER_TAB_PRESETS"] = "ПРЕСЕТЫ",
                    ["WEATHER_TAB_GAMEPLAY"] = "ГЕЙМПЛЕЙ",
                    ["WEATHER_TAB_CHANCES"] = "ШАНСЫ",
                    ["WEATHER_TAB_WEATHER"] = "ПОГОДА",
                    ["WEATHER_TAB_ATMOSPHERE"] = "АТМОСФЕРА",
                    ["WEATHER_TAB_CLOUDS"] = "ОБЛАКА",

                    ["WEATHER_PRESET_CLEAR"] = "Ясная погода",
                    ["WEATHER_PRESET_CLEAR_DESC"] = "weather.load Clear",
                    ["WEATHER_PRESET_DUST"] = "Песчаная буря",
                    ["WEATHER_PRESET_DUST_DESC"] = "weather.load Dust",
                    ["WEATHER_PRESET_FOG"] = "Густой туман",
                    ["WEATHER_PRESET_FOG_DESC"] = "weather.load Fog",
                    ["WEATHER_PRESET_OVERCAST"] = "Пасмурно",
                    ["WEATHER_PRESET_OVERCAST_DESC"] = "weather.load Overcast",
                    ["WEATHER_PRESET_RAINHEAVY"] = "Сильный ливень",
                    ["WEATHER_PRESET_RAINHEAVY_DESC"] = "weather.load RainHeavy",
                    ["WEATHER_PRESET_RAINMILD"] = "Легкий дождь",
                    ["WEATHER_PRESET_RAINMILD_DESC"] = "weather.load RainMild",
                    ["WEATHER_PRESET_STORM"] = "Грозовой шторм",
                    ["WEATHER_PRESET_STORM_DESC"] = "weather.load Storm",
                    ["WEATHER_PRESET_RESET"] = "Динамическая погода",
                    ["WEATHER_PRESET_RESET_DESC"] = "weather.reset",

                    ["WEATHER_PARAM_AUTO"] = "Авто (-1)",
                    ["WEATHER_PARAM_APPLY"] = "ПРИМЕНИТЬ",
                    ["WEATHER_PARAM_PLACEHOLDER"] = "-1.0 .. 1.0",
                    ["WEATHER_CHANCE_PLACEHOLDER"] = "0.0 .. 1.0",

                    ["PARAM_weather.wetness_rain"] = "Намокание от дождя",
                    ["PARAM_weather.wetness_snow"] = "Намокание от снега",
                    ["PARAM_weather.clear_chance"] = "Шанс ясной погоды",
                    ["PARAM_weather.dust_chance"] = "Шанс бури",
                    ["PARAM_weather.fog_chance"] = "Шанс тумана",
                    ["PARAM_weather.overcast_chance"] = "Шанс пасмурности",
                    ["PARAM_weather.storm_chance"] = "Шанс грозы",
                    ["PARAM_weather.rain_chance"] = "Шанс дождя",
                    ["PARAM_weather.rain"] = "Интенсивность дождя",
                    ["PARAM_weather.wind"] = "Скорость ветра",
                    ["PARAM_weather.thunder"] = "Частота грома",
                    ["PARAM_weather.rainbow"] = "Видимость радуги",
                    ["PARAM_weather.fog"] = "Плотность тумана",
                    ["PARAM_weather.atmosphere_rayleigh"] = "Рэлеевское рассеяние",
                    ["PARAM_weather.atmosphere_mie"] = "Аэрозольное рассеяние Ми",
                    ["PARAM_weather.atmosphere_brightness"] = "Яркость атмосферы",
                    ["PARAM_weather.atmosphere_contrast"] = "Контраст атмосферы",
                    ["PARAM_weather.atmosphere_directionality"] = "Направленность света",
                    ["PARAM_weather.cloud_size"] = "Размер облаков",
                    ["PARAM_weather.cloud_opacity"] = "Непрозрачность облаков",
                    ["PARAM_weather.cloud_coverage"] = "Покрытие облаками",
                    ["PARAM_weather.cloud_sharpness"] = "Четкость облаков",
                    ["PARAM_weather.cloud_coloring"] = "Цвет облаков",
                    ["PARAM_weather.cloud_attenuation"] = "Затухание света в облаках",
                    ["PARAM_weather.cloud_scattering"] = "Рассеяние в облаках",
                    ["PARAM_weather.cloud_brightness"] = "Яркость облаков",

                    ["WEATHER_MSG_SET"] = "<color=#52e252>Параметр погоды</color> <color=#42b0ff>{0}</color> <color=#52e252>установлен на</color> <color=#ffea6c>{1}</color>",
                    ["WEATHER_MSG_LOAD"] = "<color=#52e252>Пресет погоды</color> <color=#42b0ff>{0}</color> <color=#52e252>успешно загружен!</color>",
                    ["WEATHER_MSG_RESET"] = "<color=#52e252>Погода сброшена на автоматический динамический цикл.</color>",
                    ["WEATHER_MSG_REPORT"] = "<color=#42b0ff>Сводка погоды:</color> Дождь: {0:P0}, Ветер: {1:P0}, Туман: {2:P0}, Облака: {3:P0}",

                    // Player List Filters
                    ["FILTER_ONLINE"] = "ОНЛАЙН",
                    ["PL_FILTER_ONLINE"] = "ОНЛАЙН",
                    ["FILTER_OFFLINE"] = "ОФФЛАЙН",
                    ["PL_FILTER_OFFLINE"] = "ОФФЛАЙН",
                    ["FILTER_SLEEPING"] = "СПЯЩИЕ",
                    ["PL_FILTER_SLEEPING"] = "СПЯЩИЕ",
                    ["FILTER_ADMINS"] = "АДМИНЫ",
                    ["PL_FILTER_ADMINS"] = "АДМИНЫ",
                    ["FILTER_MODS"] = "МОДЕРАТОРЫ",
                    ["PL_FILTER_MODS"] = "МОДЕРАТОРЫ",
                    ["FILTER_ALL"] = "ВСЕ",
                    ["PL_FILTER_ALL"] = "ВСЕ",
                    ["PL_SEARCH_PLACEHOLDER"] = "Поиск по имени или SteamID...",
                    ["PL_NO_PLAYERS"] = "Игроки не найдены",

                    // User Info
                    ["UI_BACK"] = "◀ НАЗАД К СПИСКУ",
                    ["USER_BACK"] = "◀ НАЗАД К СПИСКУ",
                    ["UI_PING_IP"] = "IP: <color=#42b0ff>{0}</color> | Пинг: <color=#52e252>{1}мс</color>",
                    ["UI_HEALTH"] = "Здоровье: <color=#52e252>{0}/{1} HP</color>",
                    ["USER_HEALTH"] = "Здоровье: <color=#52e252>{0}/{1} HP</color>",
                    ["UI_RADIATION"] = "Радиация: <color=#ffb03b>{0}</color>",
                    ["UI_GRID"] = "Квадрат: <color=#42b0ff>{0}</color>",
                    ["USER_LOCATION"] = "Квадрат: <color=#42b0ff>{0}</color>",
                    ["UI_BALANCE"] = "Баланс: <color=#52e252>${0:N0}</color>",
                    ["UI_CLAN"] = "Клан: <color=#42b0ff>[{0}]</color>",
                    ["UI_CTIME"] = "Сессия: <color=#a0a0a0>{0}ч {1}м {2}с</color>",
                    ["UI_STATUS_OFFLINE"] = "Статус: <color=#e25252>Оффлайн</color>",
                    ["USER_PING"] = "Пинг",
                    ["USER_STEAM_DATE"] = "Регистрация",
                    ["USER_RUST_HOURS"] = "Часы в Rust",

                    // User Actions
                    ["ACT_TP_SELF_TO"] = "ТП К ИГРОКУ",
                    ["USER_ACT_TP_TO"] = "ТП К ИГРОКУ",
                    ["ACT_TP_TO_SELF"] = "ТП К СЕБЕ",
                    ["USER_ACT_TP_HERE"] = "ТП К СЕБЕ",
                    ["ACT_TP_AUTH"] = "ТП К АВТОРИЗАЦИИ",
                    ["USER_ACT_TP_AUTH"] = "ТП К АВТОРИЗАЦИИ",
                    ["ACT_TP_DEATH"] = "ТП К СМЕРТИ",
                    ["USER_ACT_TP_DEATH"] = "ТП К СМЕРТИ",
                    ["ACT_HEAL_100"] = "ЛЕЧЕНИЕ (100%)",
                    ["USER_ACT_HEAL_100"] = "ЛЕЧЕНИЕ (100%)",
                    ["ACT_CLEAR_RAD"] = "СНЯТЬ РАДИАЦИЮ",
                    ["USER_ACT_CLEAR_RAD"] = "СНЯТЬ РАДИАЦИЮ",
                    ["ACT_HEAL_SUCCESS"] = "<color=#52e252>Игрок {0} полностью исцелен!</color>",
                    ["ACT_CLEAR_RAD_SUCCESS"] = "<color=#52e252>Радиация снята у игрока {0}!</color>",
                    ["ACT_SPECTATE"] = "СПЕКТЕЙТ",
                    ["USER_ACT_SPECTATE"] = "СПЕКТЕЙТ",
                    ["ACT_CREATIVE"] = "КРЕАТИВ-РЕЖИМ",
                    ["USER_ACT_CREATIVE"] = "КРЕАТИВ-РЕЖИМ",
                    ["ACT_CUFF"] = "НАРУЧНИКИ",
                    ["USER_ACT_CUFF"] = "НАРУЧНИКИ",
                    ["ACT_STRIP_INV"] = "ОЧИСТИТЬ ИНВЕНТАРЬ",
                    ["USER_ACT_STRIP"] = "ОЧИСТИТЬ ИНВЕНТАРЬ",
                    ["ACT_VIEW_INV"] = "ОСМОТРЕТЬ ИНВЕНТАРЬ",
                    ["USER_ACT_VIEW_INV"] = "ОСМОТРЕТЬ ИНВЕНТАРЬ",
                    ["ACT_VIEW_BP"] = "ОСМОТРЕТЬ РЮКЗАК",
                    ["USER_ACT_VIEW_BP"] = "ОСМОТРЕТЬ РЮКЗАК",
                    ["ACT_UNLOCK_BP"] = "ИЗУЧИТЬ ЧЕРТЕЖИ",
                    ["USER_ACT_UNLOCK_BP"] = "ИЗУЧИТЬ ЧЕРТЕЖИ",
                    ["ACT_REVOKE_BP"] = "СБРОСИТЬ ЧЕРТЕЖИ",
                    ["ACT_REVOKE_BP_GRANTED"] = "СБРОСИТЬ ВЫДАННЫЕ",
                    ["USER_ACT_REVOKE_BP"] = "СБРОСИТЬ ЧЕРТЕЖИ",
                    ["ACT_KILL"] = "УБИТЬ",
                    ["USER_ACT_KILL"] = "УБИТЬ",
                    ["ACT_CREATIVE_ENABLED"] =
                        "<color=#52e252>Креатив-режим включен для {0}!</color>",
                    ["ACT_CREATIVE_DISABLED"] =
                        "<color=#e25252>Креатив-режим отключен для {0}!</color>",
                    ["ACT_CREATIVE_TARGET_ON"] =
                        "<color=#52e252>Вам выдан креатив-режим (бесплатный крафт и постройка)!</color>",
                    ["ACT_CREATIVE_TARGET_OFF"] = "<color=#e25252>Креатив-режим отключен.</color>",
                    ["ERR_NO_BACKPACK"] = "<color=#e25252>У игрока {0} не надет рюкзак!</color>",

                    // Permissions
                    ["PERM_GROUPS"] = "ГРУППЫ",
                    ["PERM_USERS"] = "ИГРОКИ",
                    ["PERM_CREATE_GROUP"] = "+ СОЗДАТЬ ГРУППУ",
                    ["PERM_CLONE_GROUP"] = "КЛОНИРОВАТЬ ГРУППУ",
                    ["PERM_REMOVE_GROUP"] = "УДАЛИТЬ ГРУППУ",
                    ["PERM_BACK_TO_PLUGINS"] = "◀ НАЗАД К ПЛАГИНАМ",
                    ["PERM_BACK_TO_USERS"] = "◀ НАЗАД К ИГРОКАМ",
                    ["PERM_GRANT_ALL"] = "ВЫДАТЬ ВСЕ",
                    ["PERM_REVOKE_ALL"] = "СНЯТЬ ВСЕ",
                    ["PERM_TOTAL_USERS"] = "Всего игроков: <color=#42b0ff>{0}</color>",
                    ["PERM_USER_GROUPS_TITLE"] = "Группы игрока {0}:",
                    ["PERM_USER_PERMS_TITLE"] = "Персональные права: <color=#52e252>{0}</color>",
                    ["PERM_DIRECT"] = "ЛИЧНО",
                    ["PERM_GROUP"] = "ГРУППА",
                    ["PERM_SEARCH_PLACEHOLDER"] = "Поиск плагинов...",
                    ["PERM_USER_SEARCH_PLACEHOLDER"] = "Поиск игроков...",

                    // Plugins
                    ["PLUGINS_RELOAD_ALL"] = "↻ ПЕРЕЗАГРУЗИТЬ ВСЕ",
                    ["PLUGINS_LOAD"] = "ЗАГРУЗИТЬ",
                    ["PLUGINS_UNLOAD"] = "ВЫГРУЗИТЬ",
                    ["PLUGINS_RELOAD"] = "ПЕРЕЗАГРУЗИТЬ",
                    ["STATUS_LOADED"] = "ЗАГРУЖЕН",
                    ["STATUS_UNLOADED"] = "ВЫГРУЖЕН",
                    ["PM_SEARCH_PLACEHOLDER"] = "Поиск плагинов...",

                    // Modals
                    ["MODAL_CONFIRM"] = "ПОДТВЕРДИТЬ",
                    ["MODAL_CANCEL"] = "ОТМЕНА",
                    ["MODAL_GROUP_NAME"] = "Введите название группы...",
                    ["MODAL_PLACEHOLDER_DEFAULT"] = "Введите значение...",
                    ["MODAL_CLONE_GROUP_TITLE"] = "Клонирование группы: {0}",
                    ["MODAL_DELETE_GROUP_TITLE"] = "Удалить группу: {0}?",
                    ["MODAL_DELETE_GROUP_DESC"] =
                        "Вы действительно хотите безвозвратно удалить группу <color=#e25252>{0}</color>?",
                    // CHANGE: Тексты подтверждения опасных действий над игроком
                    ["MODAL_CONFIRM_ACTION_TITLE"] = "ПОДТВЕРЖДЕНИЕ ДЕЙСТВИЯ",
                    ["MODAL_CONFIRM_KILL_DESC"] = "Убить игрока <color=#e25252>{0}</color>?",
                    ["MODAL_CONFIRM_STRIP_DESC"] =
                        "Очистить инвентарь игрока <color=#e25252>{0}</color>?",
                    ["MODAL_CONFIRM_REVOKE_DESC"] =
                        "Полностью сбросить чертежи игрока <color=#e25252>{0}</color>? Останутся только дефолтные.",
                    ["MODAL_CONFIRM_REVOKE_GRANTED_DESC"] =
                        "Отозвать чертежи, выданные плагином, у игрока <color=#e25252>{0}</color>?",
                    ["ACT_REVOKE_GRANTED_NO_SNAPSHOT"] =
                        "<color=#e25252>Отменить нечего:</color> для этого игрока ещё не использовалась кнопка «ИЗУЧИТЬ ЧЕРТЕЖИ», поэтому список выданных чертежей не сохранён. Нажми «ИЗУЧИТЬ ЧЕРТЕЖИ», после этого «СБРОСИТЬ ВЫДАННЫЕ» будет работать. Либо нажми «СБРОСИТЬ ЧЕРТЕЖИ» — полный сброс до дефолтных.",
                },
                this,
                "ru"
            );
        }

        private string Msg(string key, string userId = null, params object[] args)
        {
            string message = lang.GetMessage(key, this, userId);
            return (args != null && args.Length > 0) ? string.Format(message, args) : message;
        }

        #endregion

        #region Spectate & Backpack Viewing Hooks

        [HarmonyPatch(typeof(BasePlayer), "Tick_Spectator")]
        private static class SpectatorStaffPatch
        {
            private static bool Prefix(BasePlayer __instance)
            {
                if (__instance.serverInput.WasJustPressed(BUTTON.RELOAD))
                {
                    __instance.Respawn();
                    return false;
                }

                int num = 0;
                if (__instance.serverInput.WasJustPressed(BUTTON.LEFT))
                    num--;
                else if (__instance.serverInput.WasJustPressed(BUTTON.RIGHT))
                    num++;

                if (num != 0)
                {
                    __instance.SpectateOffset += num;
                    using (TimeWarning.New("UpdateSpectateTarget", 0))
                        __instance.UpdateSpectateTarget(__instance.spectateFilter);
                }

                return true;
            }
        }

        private static readonly HashSet<int> BackpackItemIds = new HashSet<int>
        {
            -907422733, // Large Backpack (largebackpack)
            -874650016, // Krieg Large Backpack (kriegbackpack)
            2068884361, // Small Backpack (smallbackpack)
        };

        private static readonly HashSet<string> BackpackShortnames = new HashSet<string>
        {
            "largebackpack",
            "kriegbackpack",
            "smallbackpack",
        };

        // CHANGE: Проверка является ли предмет одним из поддерживаемых рюкзаков (ID, shortname, флаги)
        private bool IsBackpackItem(Item item)
        {
            if (item == null || item.info == null)
                return false;

            if (BackpackItemIds.Contains(item.info.itemid))
                return true;

            if (BackpackShortnames.Contains(item.info.shortname))
                return true;

            if (item.IsBackpack())
                return true;

            return false;
        }

        // CHANGE: Получение надетого рюкзака игрока из слота одежды или инвентаря
        private Item GetEquippedBackpack(BasePlayer target)
        {
            if (target == null || target.inventory == null)
                return null;

            Item bp = target.inventory.GetAnyBackpack();
            if (bp != null && bp.contents != null && IsBackpackItem(bp))
                return bp;

            if (target.inventory.containerWear != null)
            {
                for (int i = 0; i < target.inventory.containerWear.capacity; i++)
                {
                    Item item = target.inventory.containerWear.GetSlot(i);
                    if (item != null && item.contents != null && IsBackpackItem(item))
                        return item;
                }
            }

            return null;
        }

        private readonly Dictionary<ulong, LootableCorpse> _activeInspectors =
            new Dictionary<ulong, LootableCorpse>();

        // CHANGE: Очистка временного трупа инспектора инвентаря
        private void CleanupInspector(ulong adminId)
        {
            if (_activeInspectors.TryGetValue(adminId, out LootableCorpse corpse))
            {
                _activeInspectors.Remove(adminId);
                if (corpse != null && !corpse.IsDestroyed)
                {
                    corpse.containers = null;
                    corpse.Kill();
                }
            }
        }

        // CHANGE: Открытие клиентской панели лута через Reflection для совместимости с любыми средами
        private void OpenLootPanel(BasePlayer player, string panelName)
        {
            if (player == null || !player.IsConnected)
                return;

            var rpcTargetType = typeof(BaseEntity).Assembly.GetType("RpcTarget");
            var rpcTarget = rpcTargetType
                ?.GetMethod("Player", new[] { typeof(string), typeof(BasePlayer) })
                ?.Invoke(null, new object[] { "RPC_OpenLootPanel", player });
            if (rpcTarget != null)
            {
                typeof(BaseEntity)
                    .GetMethod("ClientRPC", new[] { rpcTargetType, typeof(string) })
                    ?.Invoke(player, new object[] { rpcTarget, panelName });
            }
        }

        // CHANGE: Открытие полного инвентаря игрока (одежда, пояс, основной инвентарь)
        private void ViewInventory(BasePlayer admin, BasePlayer target)
        {
            if (admin == null || target == null || target.inventory == null)
                return;

            DestroyMenu(admin);
            CleanupInspector(admin.userID);

            PlayerLoot playerLoot = admin.inventory.loot;
            bool isLooting = playerLoot.IsLooting();
            playerLoot.containers.Clear();
            playerLoot.entitySource = null;
            playerLoot.itemSource = null;
            if (isLooting)
                playerLoot.SendImmediate();

            NextFrame(() =>
            {
                if (
                    admin == null
                    || !admin.IsConnected
                    || target == null
                    || target.inventory == null
                )
                    return;

                LootableCorpse corpse =
                    GameManager.server.CreateEntity(
                        "assets/prefabs/player/player_corpse.prefab",
                        new Vector3(admin.transform.position.x, -100f, admin.transform.position.z)
                    ) as LootableCorpse;

                if (corpse == null)
                    return;

                corpse.syncPosition = false;
                corpse.limitNetworking = true;
                corpse.playerName = $"{target.displayName}";
                corpse.playerSteamID = target.userID;
                corpse.enableSaving = false;
                corpse.Spawn();
                corpse.CancelInvoke(corpse.RemoveCorpse);
                // CHANGE: Использование SetFlagLocal для установки флага блокировки сущности трупа
                corpse.SetFlagLocal(BaseEntity.Flags.Locked, true);

                Buoyancy buoyancy;
                if (corpse.TryGetComponent<Buoyancy>(out buoyancy))
                    UnityEngine.Object.Destroy(buoyancy);

                Rigidbody rb;
                if (corpse.TryGetComponent<Rigidbody>(out rb))
                    UnityEngine.Object.Destroy(rb);

                _activeInspectors[admin.userID] = corpse;

                playerLoot.PositionChecks = false;
                playerLoot.entitySource = corpse;
                playerLoot.itemSource = null;
                playerLoot.AddContainer(target.inventory.containerMain);
                playerLoot.AddContainer(target.inventory.containerWear);
                playerLoot.AddContainer(target.inventory.containerBelt);
                playerLoot.MarkDirty();
                playerLoot.SendImmediate();

                OpenLootPanel(admin, "player_corpse");
            });
        }

        // CHANGE: Открытие содержимого контейнера рюкзака для администратора
        private void ViewContainer(BasePlayer admin, ItemContainer container)
        {
            if (admin == null || container == null)
                return;

            DestroyMenu(admin);
            PlayerLoot playerLoot = admin.inventory.loot;
            bool isLooting = playerLoot.IsLooting();
            playerLoot.containers.Clear();
            playerLoot.entitySource = null;
            playerLoot.itemSource = null;
            if (isLooting)
                playerLoot.SendImmediate();

            NextFrame(() =>
            {
                if (admin == null || !admin.IsConnected || container == null)
                    return;

                playerLoot.PositionChecks = false;
                playerLoot.entitySource = (container.entityOwner as BaseEntity) ?? admin;
                playerLoot.AddContainer(container);
                OpenLootPanel(admin, "generic_resizable");
            });
        }

        #endregion

        #region Lifecycle & Initialization Hooks

        // CHANGE: Инициализация плагина, регистрация прав, команд и обработчиков
        private void Init()
        {
            Instance = this;

            // CHANGE: Точечный патч только SpectatorStaffPatch — PatchAll(Assembly) сканировал весь файл (~2с компиляции)
            _harmony = new HarmonyLib.Harmony(Name);
            _harmony.CreateClassProcessor(typeof(SpectatorStaffPatch)).Patch();

        }

        private void OnServerInitialized()
        {
            StartHeaderRealtimeTimer();
        }

        // CHANGE: Выгрузка плагина, очистка GUI всех игроков, таймеров, инспекторов и снятие Harmony-патчей
        private void Unload()
        {
            // CHANGE: Oxide использует Harmony без UnpatchSelf() — снимаем патчи через UnpatchAll по Id экземпляра
            _harmony?.UnpatchAll(_harmony.Id);
            _harmony = null;

            _headerRealtimeTimer?.Destroy();
            _headerRealtimeTimer = null;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player != null)
                    DestroyMenu(player);
            }

            foreach (ulong adminId in _activeInspectors.Keys.ToList())
            {
                CleanupInspector(adminId);
            }
            _activeInspectors.Clear();

            _sessions.Clear();
            _tpMarkerPlayers.Clear();
            // CHANGE: Снятие флага CreativeMode и клиентского креатив-UI у всех креатив-игроков до выгрузки —
            // иначе после перезагрузки плагина игроки остаются с флагом, но уже без отслеживания плагином
            foreach (ulong creativeId in _creativePlayers.ToList())
            {
                BasePlayer creativePlayer = BasePlayer.FindByID(creativeId);
                if (creativePlayer == null)
                    continue;

                creativePlayer.SetPlayerFlag(BasePlayer.PlayerFlags.CreativeMode, false);
                if (creativePlayer.IsConnected)
                    creativePlayer.Command("debug.setcreative_ui", false);
                creativePlayer.SendNetworkUpdateImmediate();
            }
            // CHANGE: Перед очисткой коллекции возвращаем нативные конвары Creative.* в исходное состояние
            ApplyCreativeConvars(false);
            _creativePlayers.Clear();
            _cuffedPlayers.Clear();
            _cachedSteamInfo.Clear();
            Instance = null;
        }

        // CHANGE: Очистка сессии и закрытие интерфейса при отключении игрока
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
                return;

            DestroyMenu(player);
            CleanupInspector(player.userID);
            _sessions.Remove(player.userID);
            _tpMarkerPlayers.Remove(player.userID);
            _creativePlayers.Remove(player.userID);
            // CHANGE: Если креатив-игроков не осталось — возвращаем нативные конвары Creative.* в выключенное состояние
            RestoreCreativeConvarsIfNoneLeft();
            _cuffedPlayers.Remove(player.userID);
        }

        // CHANGE: Закрытие меню при смерти игрока
        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null)
                return;

            if (_sessions.ContainsKey(player.userID))
            {
                DestroyMenu(player);
                _sessions.Remove(player.userID);
            }
        }

        // CHANGE: Телепортация на маркер карты при активном режиме TeleportToMarker
        private void OnMapMarkerAdd(BasePlayer player, ProtoBuf.MapNote note)
        {
            if (player == null || note == null)
                return;

            if (_tpMarkerPlayers.Contains(player.userID) && HasAccess(player))
            {
                Vector3 worldPos = note.worldPosition;
                if (worldPos != Vector3.zero)
                {
                    float height = TerrainMeta.HeightMap.GetHeight(worldPos);
                    worldPos.y = Mathf.Max(worldPos.y, height) + 1.5f;
                    player.Teleport(worldPos);
                }
            }
        }

        #endregion

        #region Commands & Hotkey Handlers

        // CHANGE: Хук консольных команд клиента для перехвата горячих клавиш X (swapseats) и F (lighttoggle) с неблокирующим NextTick
        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.cmd == null)
                return null;

            string name = arg.cmd.Name;
            string fullName = arg.cmd.FullName;

            if (_config.General.ButtonToHook == ButtonHook.X)
            {
                if (name == "swapseats" || fullName == "vehicle.swapseats")
                {
                    BasePlayer player = arg.Player();
                    if (player != null && !player.isMounted && HasAccess(player))
                    {
                        NextTick(() =>
                        {
                            if (player != null && player.IsConnected)
                                ToggleMenu(player);
                        });
                        return true;
                    }
                }
            }
            else if (_config.General.ButtonToHook == ButtonHook.F)
            {
                if (
                    name == "lighttoggle"
                    || fullName == "inventory.lighttoggle"
                    || name == "lighttoggle_sv"
                    || fullName == "inventory.lighttoggle_sv"
                )
                {
                    BasePlayer player = arg.Player();
                    if (player != null && !player.IsDucked() && HasAccess(player))
                    {
                        NextTick(() =>
                        {
                            if (player != null && player.IsConnected)
                                ToggleMenu(player);
                        });
                        return true;
                    }
                }
            }

            return null;
        }

        [ChatCommand("admin")]
        private void ChatCmd_AdminDefault(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasAccess(player))
                return;
            ToggleMenu(player);
        }

        private void ChatCmd_RAdminMenu(BasePlayer player, string command, string[] args)
        {
            if (player == null || !HasAccess(player))
                return;
            ToggleMenu(player);
        }

        [ConsoleCommand("radminmenu.open")]
        private void ConsoleCmd_OpenMenu(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;
            OpenMenu(player);
        }

        [ConsoleCommand("radminmenu.toggle")]
        private void ConsoleCmd_ToggleMenu(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;
            ToggleMenu(player);
        }

        private void ConsoleCmd_SwapSeatsHook(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null && HasAccess(player) && !player.isMounted)
            {
                NextTick(() =>
                {
                    if (player != null && player.IsConnected)
                        ToggleMenu(player);
                });
            }
            else
                ConVar.vehicle.swapseats(arg);
        }

        private void ConsoleCmd_LightToggleHook(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player != null && HasAccess(player) && !player.IsDucked())
            {
                NextTick(() =>
                {
                    if (player != null && player.IsConnected)
                        ToggleMenu(player);
                });
            }
            else
                ConVar.Inventory.lighttoggle_sv(arg);
        }

        /// <summary>
        /// Проверяет право доступа к меню администратора.
        /// Доступ разрешён при уровне аутентификации Rust >= 2 (серверный администратор).
        /// </summary>
        /// <param name="player">Игрок, для которого проверяется доступ.</param>
        /// <returns>true если игрок является администратором сервера (authLevel >= 2).</returns>
        // CHANGE: Доступ определяется исключительно по authLevel >= 2; привилегии oxide не требуются
        private bool HasAccess(BasePlayer player)
        {
            if (player == null)
                return false;
            return player.net?.connection != null
                && player.net.connection.authLevel >= 2;
        }

        private AdminSession GetSession(BasePlayer player)
        {
            if (!_sessions.TryGetValue(player.userID, out AdminSession session))
            {
                session = new AdminSession();
                _sessions[player.userID] = session;
            }
            return session;
        }

        private void ToggleMenu(BasePlayer player)
        {
            if (player == null)
                return;
            if (_sessions.ContainsKey(player.userID))
            {
                DestroyMenu(player);
                _sessions.Remove(player.userID);
            }
            else
            {
                OpenMenu(player);
            }
        }

        private void OpenMenu(BasePlayer player)
        {
            AdminSession session = GetSession(player);
            // CHANGE: Помечаем сессию как открытую — фоновые таймеры обновляют только открытое меню
            session.MenuOpen = true;
            RenderFullMenu(player);
        }

        private void DestroyMenu(BasePlayer player)
        {
            if (player == null)
                return;
            // CHANGE: Меню закрыто — таймеры реального времени прекращают обновление UI этого игрока
            GetSession(player).MenuOpen = false;
            CuiHelper.DestroyUi(player, LayerMain);
            CuiHelper.DestroyUi(player, LayerNavigation);
            CuiHelper.DestroyUi(player, LayerNavButtons);
            CuiHelper.DestroyUi(player, LayerHeader);
            CuiHelper.DestroyUi(player, LayerHeaderTitle);
            CuiHelper.DestroyUi(player, LayerHeaderInfo);
            CuiHelper.DestroyUi(player, LayerContent);
            CuiHelper.DestroyUi(player, LayerContentBody);
            CuiHelper.DestroyUi(player, LayerModal);
        }

        #endregion

        #region UI Rendering Core

        // CHANGE: Единая пакетная сборка полного интерфейса меню в один CuiElementContainer (1 RPC пакет)
        private void RenderFullMenu(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            DestroyMenu(player);

            AdminSession session = GetSession(player);
            var container = new CuiElementContainer();

            // 1. Полноэкранный слой-оверлей
            container.Add(
                new CuiPanel
                {
                    Image = { Color = _config.General.OverlayColor },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin = "0 0",
                        OffsetMax = "0 0",
                    },
                    CursorEnabled = true,
                },
                "Overlay",
                LayerMain
            );

            // 2. Навигация и кнопки
            AddNavigationBase(container, player);
            AddNavigationButtons(container, player, session);

            // 3. Шапка, заголовок и статистика сервера
            AddHeaderBase(container, player);
            AddHeaderTitle(container, player, session);
            int online = BasePlayer.activePlayerList.Count;
            int max = ConVar.Server.maxplayers;
            int sleepers = BasePlayer.sleepingPlayerList.Count;
            int fps = Performance.current.frameRate;
            AddHeaderInfo(container, player, online, max, sleepers, fps);

            // 4. Базовая панель контента и динамическое содержимое
            AddContentBase(container, player);
            AddContentBody(container, player, session);

            // 5. Единая отправка всех слоев клиенту
            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Добавление базового каркаса панели навигации в контейнер
        private void AddNavigationBase(CuiElementContainer container, BasePlayer player)
        {
            var navCfg = _config.Navigation;
            int halfW = navCfg.Panel.Width / 2;
            int halfH = navCfg.Panel.Height / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = navCfg.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{navCfg.Panel.OffsetX - halfW} {navCfg.Panel.OffsetY - halfH}",
                        OffsetMax =
                            $"{navCfg.Panel.OffsetX + halfW} {navCfg.Panel.OffsetY + halfH}",
                    },
                },
                LayerMain,
                LayerNavigation
            );

            // Логотип / Название меню
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = Msg("NAV_HEADER_TITLE", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = navCfg.Header.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = navCfg.Header.TextColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "1 1",
                        OffsetMin = $"0 {-navCfg.Header.Height}",
                        OffsetMax = "0 0",
                    },
                },
                LayerNavigation
            );
        }

        // CHANGE: Добавление кнопок навигации в контейнер
        private void AddNavigationButtons(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var navCfg = _config.Navigation;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin = "0 0",
                        OffsetMax = "0 0",
                    },
                },
                LayerNavigation,
                LayerNavButtons
            );

            // Кнопки навигации
            var navButtons = new List<(string key, string langKey)>
            {
                ("quickmenu", "NAV_QUICK"),
                ("players", "NAV_PLAYERS"),
                ("permissions", "NAV_PERMISSIONS"),
                ("plugins", "NAV_PLUGINS"),
            };

            int startY = navCfg.Buttons.StartY - navCfg.Buttons.Height;
            int btnHalfW = navCfg.Buttons.Width / 2;

            for (int i = 0; i < navButtons.Count; i++)
            {
                var (catKey, langKey) = navButtons[i];
                bool isActive = (session.CurrentCategory == catKey);
                string btnColor = isActive
                    ? navCfg.Buttons.ActiveColor
                    : navCfg.Buttons.InactiveColor;
                int btnY = startY - (i * (navCfg.Buttons.Height + navCfg.Buttons.Spacing));
                int btnHalfH = navCfg.Buttons.Height / 2;

                string cmdAction = $"radminmenu.nav {catKey}";

                container.Add(
                    new CuiButton
                    {
                        Button = { Command = cmdAction, Color = btnColor },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{-btnHalfW} {btnY - btnHalfH}",
                            OffsetMax = $"{btnHalfW} {btnY + btnHalfH}",
                        },
                        Text =
                        {
                            Text = Msg(langKey, player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = navCfg.Buttons.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = navCfg.Buttons.TextColor,
                        },
                    },
                    LayerNavButtons,
                    $"NavBtn_{catKey}"
                );
            }
        }

        // CHANGE: Отрисовка навигации при точечном обновлении
        private void RenderNavigation(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            CuiHelper.DestroyUi(player, LayerNavigation);

            var container = new CuiElementContainer();
            AddNavigationBase(container, player);
            AddNavigationButtons(container, player, GetSession(player));

            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Точечное обновление кнопок навигации без перерисовки всей панели навигации
        private void RenderNavigationButtons(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            CuiHelper.DestroyUi(player, LayerNavButtons);

            var container = new CuiElementContainer();
            AddNavigationButtons(container, player, GetSession(player));

            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Добавление базового каркаса шапки в контейнер
        private void AddHeaderBase(CuiElementContainer container, BasePlayer player)
        {
            var headerCfg = _config.Header;
            int halfW = headerCfg.Panel.Width / 2;
            int halfH = headerCfg.Panel.Height / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = headerCfg.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{headerCfg.Panel.OffsetX - halfW} {headerCfg.Panel.OffsetY - halfH}",
                        OffsetMax =
                            $"{headerCfg.Panel.OffsetX + halfW} {headerCfg.Panel.OffsetY + halfH}",
                    },
                },
                LayerMain,
                LayerHeader
            );
        }

        // CHANGE: Добавление заголовка шапки в контейнер
        private void AddHeaderTitle(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var headerCfg = _config.Header;

            string categoryTitle = session.CurrentCategory == "userinfo"
                ? Msg("NAV_USERINFO", player.UserIDString)
                : Msg($"NAV_{session.CurrentCategory.ToUpper()}", player.UserIDString);

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = categoryTitle,
                        Align = TextAnchor.MiddleLeft,
                        FontSize = headerCfg.Title.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = headerCfg.Title.TextColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "0.5 1",
                        OffsetMin = $"{headerCfg.Title.OffsetX} 0",
                        OffsetMax = "0 0",
                    },
                },
                LayerHeader,
                LayerHeaderTitle
            );
        }

        // CHANGE: Добавление блока статистики сервера в контейнер
        private void AddHeaderInfo(
            CuiElementContainer container,
            BasePlayer player,
            int online,
            int max,
            int sleepers,
            int fps
        )
        {
            var headerCfg = _config.Header;

            string infoText = Msg("HEADER_ONLINE", player.UserIDString, online, max, sleepers, fps);

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = infoText,
                        Align = TextAnchor.MiddleRight,
                        FontSize = headerCfg.Info.FontSize,
                        Font = "robotocondensed-regular.ttf",
                        Color = headerCfg.Info.TextColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0",
                        AnchorMax = "1 1",
                        OffsetMin = "0 0",
                        OffsetMax = $"{headerCfg.Info.OffsetX} 0",
                    },
                },
                LayerHeader,
                LayerHeaderInfo
            );
        }

        // CHANGE: Отрисовка всей шапки при точечном вызове
        private void RenderHeader(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            CuiHelper.DestroyUi(player, LayerHeader);

            var container = new CuiElementContainer();
            AdminSession session = GetSession(player);
            AddHeaderBase(container, player);
            AddHeaderTitle(container, player, session);

            int online = BasePlayer.activePlayerList.Count;
            int max = ConVar.Server.maxplayers;
            int sleepers = BasePlayer.sleepingPlayerList.Count;
            int fps = Performance.current.frameRate;

            AddHeaderInfo(container, player, online, max, sleepers, fps);

            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Точечное обновление названия активной категории без перерисовки всей шапки
        private void UpdateHeaderTitleOnly(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            CuiHelper.DestroyUi(player, LayerHeaderTitle);

            AdminSession session = GetSession(player);
            var container = new CuiElementContainer();
            AddHeaderTitle(container, player, session);

            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Метод обновления текста онлайна и FPS без перерисовки всей шапки
        private void UpdateHeaderInfoOnly(
            BasePlayer player,
            int online,
            int max,
            int sleepers,
            int fps
        )
        {
            if (player == null || !player.IsConnected)
                return;

            CuiHelper.DestroyUi(player, LayerHeaderInfo);

            var container = new CuiElementContainer();
            AddHeaderInfo(container, player, online, max, sleepers, fps);

            CuiHelper.AddUi(player, container);
        }

        private Timer _headerRealtimeTimer;

        // CHANGE: Запуск фонового таймера динамического обновления FPS и онлайна в шапке
        private void StartHeaderRealtimeTimer()
        {
            _headerRealtimeTimer?.Destroy();
            float interval =
                _config.Header.UpdateInterval > 0.1f ? _config.Header.UpdateInterval : 1f;
            _headerRealtimeTimer = timer.Every(interval, UpdateHeadersRealtime);
        }

        private void UpdateHeadersRealtime()
        {
            if (_sessions.Count == 0)
                return;

            int online = BasePlayer.activePlayerList.Count;
            int max = ConVar.Server.maxplayers;
            int sleepers = BasePlayer.sleepingPlayerList.Count;
            int fps = Performance.current.frameRate;

            foreach (ulong userId in _sessions.Keys)
            {
                BasePlayer p = BasePlayer.FindByID(userId);
                if (p != null && p.IsConnected && GetSession(p).MenuOpen)
                {
                    UpdateHeaderInfoOnly(p, online, max, sleepers, fps);
                    UpdateQuickMenuTimeOnly(p);
                    // CHANGE: Динамическое обновление параметров выбранного игрока (сессия, радиация, HP)
                    UpdateUserInfoRealtime(p);
                }
            }
        }

        /// <summary>
        /// Выполняет динамическое обновление характеристик игрока в реальном времени (сессия, здоровье, радиация, пинг).
        /// </summary>
        /// <param name="player">Администратор, просматривающий карточку игрока</param>
        // CHANGE: Точечное обновление характеристик игрока (сессия, здоровье, радиация, пинг) в реальном времени
        private void UpdateUserInfoRealtime(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            AdminSession session = GetSession(player);
            if (session == null || session.CurrentCategory != "userinfo" || session.SelectedUserId == 0)
                return;

            IPlayer target = covalence.Players.FindPlayerById(session.SelectedUserId.ToString());
            if (target == null)
                return;

            BasePlayer targetBasePlayer = BasePlayer.FindAwakeOrSleeping(target.Id);
            var uiCfg = _config.UserInfo;

            CuiHelper.DestroyUi(player, "UI_Info_DetailsLeft");
            CuiHelper.DestroyUi(player, "UI_Info_DetailsRight");

            float hp = targetBasePlayer != null ? targetBasePlayer.health : 0;
            float maxHp = targetBasePlayer != null ? targetBasePlayer.MaxHealth() : 100;
            float rad = (targetBasePlayer != null && targetBasePlayer.metabolism != null && targetBasePlayer.metabolism.radiation_poison != null)
                ? targetBasePlayer.metabolism.radiation_poison.value
                : 0f;

            string grid = targetBasePlayer != null
                ? MapHelper.PositionToString(targetBasePlayer.transform.position)
                : "Unknown";

            // CHANGE: IP видят только администраторы (authLevel >= 2)
            bool canViewIp = HasAccess(player);

            string ipAddress = targetBasePlayer?.net?.connection?.ipaddress ?? target.Address ?? "N/A";
            int ping = target.Ping;

            string ipPing = canViewIp
                ? Msg("UI_PING_IP", player.UserIDString, ipAddress, ping)
                : "IP: ***.***.***.***";

            string detailsLeft =
                $"{Msg("UI_HEALTH", player.UserIDString, (int)hp, (int)maxHp)}\n{Msg("UI_RADIATION", player.UserIDString, (int)rad)}\n{Msg("UI_GRID", player.UserIDString, grid)}\n{ipPing}";

            string detailsRight = "";
            if (Economics != null)
            {
                double bal = Economics.Call<double>("Balance", session.SelectedUserId);
                detailsRight += Msg("UI_BALANCE", player.UserIDString, bal) + "\n";
            }
            if (Clans != null)
            {
                string tag = Clans.Call<string>("GetClanOf", target.Id);
                if (!string.IsNullOrEmpty(tag))
                    detailsRight += Msg("UI_CLAN", player.UserIDString, tag) + "\n";
            }
            if (targetBasePlayer != null && targetBasePlayer.IsConnected && targetBasePlayer.Connection != null)
            {
                TimeSpan span = TimeSpan.FromSeconds(
                    targetBasePlayer.Connection.GetSecondsConnected()
                );
                detailsRight +=
                    Msg("UI_CTIME", player.UserIDString, span.Hours, span.Minutes, span.Seconds)
                    + "\n";
            }
            else
            {
                detailsRight += Msg("UI_STATUS_OFFLINE", player.UserIDString) + "\n";
            }

            var container = new CuiElementContainer();

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = detailsLeft,
                        Align = TextAnchor.MiddleLeft,
                        FontSize = uiCfg.Details.DetailsFontSize,
                        Font = "robotocondensed-regular.ttf",
                        Color = uiCfg.Details.DetailsColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "0.5 1",
                        OffsetMin = $"{uiCfg.Details.LeftColumnOffsetMinX} -25",
                        OffsetMax = "0 0",
                    },
                },
                "UI_InfoPanel",
                "UI_Info_DetailsLeft"
            );

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = detailsRight,
                        Align = TextAnchor.MiddleLeft,
                        FontSize = uiCfg.Details.DetailsFontSize,
                        Font = "robotocondensed-regular.ttf",
                        Color = uiCfg.Details.DetailsColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0",
                        AnchorMax = "1 1",
                        OffsetMin = "0 -25",
                        OffsetMax = $"{uiCfg.Details.RightColumnOffsetMaxX} 0",
                    },
                },
                "UI_InfoPanel",
                "UI_Info_DetailsRight"
            );

            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Добавление постоянной фоновой панели контента в контейнер
        private void AddContentBase(CuiElementContainer container, BasePlayer player)
        {
            var contentCfg = _config.Content;
            int halfW = contentCfg.Panel.Width / 2;
            int halfH = contentCfg.Panel.Height / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = contentCfg.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{contentCfg.Panel.OffsetX - halfW} {contentCfg.Panel.OffsetY - halfH}",
                        OffsetMax =
                            $"{contentCfg.Panel.OffsetX + halfW} {contentCfg.Panel.OffsetY + halfH}",
                    },
                },
                LayerMain,
                LayerContent
            );
        }

        // CHANGE: Отрисовка постоянной фоновой панели контента один раз при открытии меню
        private void RenderContentBase(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            CuiHelper.DestroyUi(player, LayerContent);

            var container = new CuiElementContainer();
            AddContentBase(container, player);

            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Добавление динамического тела контента и категории в контейнер
        private void AddContentBody(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            container.Add(
                new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin = "0 0",
                        OffsetMax = "0 0",
                    },
                },
                LayerContent,
                LayerContentBody
            );

            switch (session.CurrentCategory)
            {
                case "quickmenu":
                    RenderQuickMenuContent(container, player, session);
                    break;
                case "weather":
                    RenderWeatherManagerContent(container, player, session);
                    break;
                case "players":
                    RenderPlayerListContent(container, player, session);
                    break;
                case "userinfo":
                    RenderUserInfoContent(container, player, session);
                    break;
                case "permissions":
                    RenderPermissionManagerContent(container, player, session);
                    break;
                case "plugins":
                    RenderPluginManagerContent(container, player, session);
                    break;
            }
        }

        // CHANGE: Отрисовка динамического тела контента без уничтожения базовой фоновой панели
        private void RenderContent(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            CuiHelper.DestroyUi(player, LayerContentBody);

            var container = new CuiElementContainer();
            AdminSession session = GetSession(player);
            AddContentBody(container, player, session);

            CuiHelper.AddUi(player, container);
        }

        #endregion

        #region Content Page: Quick Menu

        // CHANGE: Отрисовка карточек Главного меню через вертикальный ScrollView с автоматическим расчетом высоты
        private void RenderQuickMenuContent(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var qmCfg = _config.QuickMenu;
            const int scrollPaddingTop = 10;
            const int scrollPaddingBottom = 10;
            const int scrollCardSpacing = 12;
            const int scrollHalfW = 330;
            const int scrollHalfH = 210;

            // CHANGE: Высоты карточек фиксированные — кнопки в один ряд с горизонтальным скроллом
            var cardHeights = new[]
            {
                qmCfg.TeleportCard.Panel.Height,
                qmCfg.ActionsCard.Panel.Height,
                qmCfg.EventsCard.Panel.Height,
                qmCfg.TimeCard.Panel.Height,
                qmCfg.WeatherCard.Panel.Height,
            };

            float totalHeight = scrollPaddingTop + scrollPaddingBottom;
            for (int i = 0; i < cardHeights.Length; i++)
            {
                totalHeight += cardHeights[i];
                if (i < cardHeights.Length - 1)
                    totalHeight += scrollCardSpacing;
            }

            container.Add(
                new CuiElement
                {
                    Parent = LayerContentBody,
                    Name = "QuickMenu_Scroll",
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{-scrollHalfW} {-5 - scrollHalfH}",
                            OffsetMax = $"{scrollHalfW} {-5 + scrollHalfH}",
                        },
                        new CuiImageComponent
                        {
                            Color = "0 0 0 0",
                        },
                        new CuiRectMask2DComponent(),
                        new CuiScrollViewComponent
                        {
                            Vertical = true,
                            Horizontal = false,
                            Inertia = true,
                            MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                            ScrollSensitivity = SCROLL_SENSITIVITY,
                            ContentTransform = new CuiRectTransformComponent
                            {
                                AnchorMin = "0 1",
                                AnchorMax = "1 1",
                                OffsetMin = $"0 {-totalHeight}",
                                OffsetMax = "0 0",
                            },
                        },
                    },
                }
            );

            float curCardTop = scrollPaddingTop;

            // 1. Карточка: Телепортация
            RenderQuickMenuStandardCard(
                container,
                player,
                "QM_Card_Teleport",
                qmCfg.TeleportCard.Panel,
                qmCfg.TeleportCard.BackgroundColor,
                qmCfg.TeleportCard.TitleOffsetX,
                qmCfg.TeleportCard.TitleOffsetY,
                qmCfg.TeleportCard.TitleWidth,
                qmCfg.TeleportCard.TitleHeight,
                qmCfg.TeleportCard.TitleFontSize,
                qmCfg.TeleportCard.TitleColor,
                qmCfg.TeleportCard.ButtonWidth,
                qmCfg.TeleportCard.ButtonHeight,
                qmCfg.TeleportCard.ButtonsOffsetY,
                qmCfg.TeleportCard.ButtonSpacingX,
                qmCfg.TeleportCard.ButtonFontSize,
                qmCfg.TeleportCard.ButtonTextColor,
                Msg("QM_GROUP_TELEPORT", player.UserIDString),
                new List<(string label, string cmd, string color)>
                {
                    (
                        Msg("QM_TP_MARKER", player.UserIDString),
                        "radminmenu.qm tp_marker",
                        _tpMarkerPlayers.Contains(player.userID)
                            ? qmCfg.TeleportCard.ButtonActiveColor
                            : qmCfg.TeleportCard.ButtonDefaultColor
                    ),
                    (
                        Msg("QM_TP_DEATH", player.UserIDString),
                        "radminmenu.qm tp_death",
                        qmCfg.TeleportCard.ButtonDefaultColor
                    ),
                    (
                        Msg("QM_TP_SPAWN", player.UserIDString),
                        "radminmenu.qm tp_spawn",
                        qmCfg.TeleportCard.ButtonDefaultColor
                    ),
                },
                curCardTop
            );
            curCardTop += qmCfg.TeleportCard.Panel.Height + scrollCardSpacing;

            // 2. Карточка: Действия администратора (самоубийство, god и vanish исключены)
            RenderQuickMenuStandardCard(
                container,
                player,
                "QM_Card_Actions",
                qmCfg.ActionsCard.Panel,
                qmCfg.ActionsCard.BackgroundColor,
                qmCfg.ActionsCard.TitleOffsetX,
                qmCfg.ActionsCard.TitleOffsetY,
                qmCfg.ActionsCard.TitleWidth,
                qmCfg.ActionsCard.TitleHeight,
                qmCfg.ActionsCard.TitleFontSize,
                qmCfg.ActionsCard.TitleColor,
                qmCfg.ActionsCard.ButtonWidth,
                qmCfg.ActionsCard.ButtonHeight,
                qmCfg.ActionsCard.ButtonsOffsetY,
                qmCfg.ActionsCard.ButtonSpacingX,
                qmCfg.ActionsCard.ButtonFontSize,
                qmCfg.ActionsCard.ButtonTextColor,
                Msg("QM_GROUP_ACTIONS", player.UserIDString),
                new List<(string label, string cmd, string color)>
                {
                    (
                        Msg("QM_HEAL", player.UserIDString),
                        "radminmenu.qm heal",
                        qmCfg.ActionsCard.HealButtonColor
                    ),
                    (
                        Msg("QM_REPAIR", player.UserIDString),
                        "radminmenu.qm repair",
                        qmCfg.ActionsCard.RepairButtonColor
                    ),
                    (
                        Msg("QM_CLEAR_INV", player.UserIDString),
                        "radminmenu.qm clear_inv",
                        qmCfg.ActionsCard.ClearInvButtonColor
                    ),
                    // CHANGE: Кнопка креатив-режима перенесена в Быстрое меню админа (переключает креатив для самого админа)
                    (
                        Msg("QM_CREATIVE", player.UserIDString),
                        "radminmenu.qm creative",
                        _creativePlayers.Contains(player.userID)
                            ? qmCfg.ActionsCard.CreativeActiveButtonColor
                            : qmCfg.ActionsCard.CreativeButtonColor
                    ),
                },
                curCardTop
            );
            // CHANGE: Кнопки в один ряд с горизонтальным скроллом — высота карточки фиксированная
            curCardTop += qmCfg.ActionsCard.Panel.Height + scrollCardSpacing;

            // 3. Карточка: События
            RenderQuickMenuStandardCard(
                container,
                player,
                "QM_Card_Events",
                qmCfg.EventsCard.Panel,
                qmCfg.EventsCard.BackgroundColor,
                qmCfg.EventsCard.TitleOffsetX,
                qmCfg.EventsCard.TitleOffsetY,
                qmCfg.EventsCard.TitleWidth,
                qmCfg.EventsCard.TitleHeight,
                qmCfg.EventsCard.TitleFontSize,
                qmCfg.EventsCard.TitleColor,
                qmCfg.EventsCard.ButtonWidth,
                qmCfg.EventsCard.ButtonHeight,
                qmCfg.EventsCard.ButtonsOffsetY,
                qmCfg.EventsCard.ButtonSpacingX,
                qmCfg.EventsCard.ButtonFontSize,
                qmCfg.EventsCard.ButtonTextColor,
                Msg("QM_GROUP_EVENTS", player.UserIDString),
                new List<(string label, string cmd, string color)>
                {
                    (
                        Msg("QM_HELI", player.UserIDString),
                        "radminmenu.qm heli",
                        qmCfg.EventsCard.ButtonColor
                    ),
                    (
                        Msg("QM_BRADLEY", player.UserIDString),
                        "radminmenu.qm bradley",
                        qmCfg.EventsCard.ButtonColor
                    ),
                    (
                        Msg("QM_CARGO", player.UserIDString),
                        "radminmenu.qm cargo",
                        qmCfg.EventsCard.ButtonColor
                    ),
                    // CHANGE: Добавлены вызов самолёта (supply.call) и чинука (spawn ch47scientist)
                    (
                        Msg("QM_AIRDROP", player.UserIDString),
                        "radminmenu.qm airdrop",
                        qmCfg.EventsCard.ButtonColor
                    ),
                    (
                        Msg("QM_CHINOOK", player.UserIDString),
                        "radminmenu.qm chinook",
                        qmCfg.EventsCard.ButtonColor
                    ),
                },
                curCardTop
            );
            // CHANGE: Кнопки в один ряд с горизонтальным скроллом — высота карточки фиксированная
            curCardTop += qmCfg.EventsCard.Panel.Height + scrollCardSpacing;

            // 4. Карточка: Управление временем суток
            RenderQuickMenuTimeCard(container, player, session, curCardTop);
            curCardTop += qmCfg.TimeCard.Panel.Height + scrollCardSpacing;

            // 5. Карточка: Управление погодой
            RenderQuickMenuWeatherCard(container, player, session, curCardTop);
        }

        // CHANGE: Кнопки карточки — один ряд в горизонтальном ScrollView (перенос на вторую строку удалён)
        private void RenderQuickMenuStandardCard(
            CuiElementContainer container,
            BasePlayer player,
            string cardName,
            PanelSettings panel,
            string bgColor,
            int titleOffsetX,
            int titleOffsetY,
            int titleWidth,
            int titleHeight,
            int titleFontSize,
            string titleColor,
            int btnWidth,
            int btnHeight,
            int btnsOffsetY,
            int btnSpacingX,
            int btnFontSize,
            string btnTextColor,
            string titleText,
            List<(string label, string cmd, string color)> buttons,
            float cardTop
        )
        {
            int cardHalfW = panel.Width / 2;
            int titleHalfH = titleHeight / 2;
            const int sidePadding = 16;
            int btnHalfH = btnHeight / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = bgColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 1",
                        AnchorMax = "0.5 1",
                        OffsetMin = $"{panel.OffsetX - cardHalfW} {-cardTop - panel.Height}",
                        OffsetMax = $"{panel.OffsetX + cardHalfW} {-cardTop}",
                    },
                },
                "QuickMenu_Scroll",
                cardName
            );

            // Заголовок карточки
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = titleText,
                        FontSize = titleFontSize,
                        Font = "robotocondensed-bold.ttf",
                        Align = TextAnchor.MiddleLeft,
                        Color = titleColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0.5",
                        AnchorMax = "0 0.5",
                        OffsetMin = $"{titleOffsetX} {titleOffsetY - titleHalfH}",
                        OffsetMax = $"{titleOffsetX + titleWidth} {titleOffsetY + titleHalfH}",
                    },
                },
                cardName,
                $"{cardName}_Title"
            );

            // CHANGE: Горизонтальный скролл ряда кнопок — все кнопки в одну строку конфиговой ширины,
            // избыток прокручивается по горизонтали внутри карточки
            int scrollW = panel.Width - sidePadding * 2;
            int totalBtnsW = buttons.Count * btnWidth + (buttons.Count - 1) * btnSpacingX;
            string scrollName = $"{cardName}_BtnScroll";
            container.Add(
                new CuiElement
                {
                    Name = scrollName,
                    Parent = cardName,
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin = $"{sidePadding} {btnsOffsetY - btnHalfH}",
                            OffsetMax = $"{sidePadding + scrollW} {btnsOffsetY + btnHalfH}",
                        },
                        new CuiImageComponent { Color = "0 0 0 0" },
                        new CuiRectMask2DComponent(),
                        new CuiScrollViewComponent
                        {
                            Vertical = false,
                            Horizontal = true,
                            Inertia = true,
                            MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                            ScrollSensitivity = SCROLL_SENSITIVITY,
                            ContentTransform = new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 1",
                                OffsetMin = "0 0",
                                OffsetMax = $"{totalBtnsW} 0",
                            },
                        },
                    },
                }
            );

            for (int i = 0; i < buttons.Count; i++)
            {
                var btn = buttons[i];
                int btnX = i * (btnWidth + btnSpacingX);

                container.Add(
                    new CuiButton
                    {
                        Button = { Command = btn.cmd, Color = btn.color },
                        RectTransform =
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin = $"{btnX} {-btnHalfH}",
                            OffsetMax = $"{btnX + btnWidth} {btnHalfH}",
                        },
                        Text =
                        {
                            Text = btn.label,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = btnFontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = btnTextColor,
                        },
                    },
                    scrollName,
                    $"{cardName}_Btn_{i}"
                );
            }
        }

        // CHANGE: Точечное обновление кнопки стандартной карточки Быстрого меню без пересоздания ScrollView —
        // сохраняет позицию скролла при кликах (кнопки лежат в горизонтальном скролле ряда, геометрия идентична RenderQuickMenuStandardCard)
        private void RefreshQuickMenuCardButton(
            BasePlayer player,
            string cardName,
            int btnWidth,
            int btnHeight,
            int btnSpacingX,
            int btnFontSize,
            string btnTextColor,
            int buttonIndex,
            string label,
            string cmd,
            string color
        )
        {
            int btnHalfH = btnHeight / 2;
            int btnX = buttonIndex * (btnWidth + btnSpacingX);

            CuiHelper.DestroyUi(player, $"{cardName}_Btn_{buttonIndex}");
            var container = new CuiElementContainer();
            container.Add(
                new CuiButton
                {
                    Button = { Command = cmd, Color = color },
                    RectTransform =
                    {
                        AnchorMin = "0 0.5",
                        AnchorMax = "0 0.5",
                        OffsetMin = $"{btnX} {-btnHalfH}",
                        OffsetMax = $"{btnX + btnWidth} {btnHalfH}",
                    },
                    Text =
                    {
                        Text = label,
                        Align = TextAnchor.MiddleCenter,
                        FontSize = btnFontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = btnTextColor,
                    },
                },
                $"{cardName}_BtnScroll",
                $"{cardName}_Btn_{buttonIndex}"
            );
            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Отрисовка карточки времени суток с динамическим тикером и одинаковыми с другими карточками габаритами
        private void RenderQuickMenuTimeCard(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session,
            float cardTop
        )
        {
            var timeCfg = _config.QuickMenu.TimeCard;
            string cardName = "QM_TimePanel";

            int cardHalfW = timeCfg.Panel.Width / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = timeCfg.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 1",
                        AnchorMax = "0.5 1",
                        OffsetMin =
                            $"{timeCfg.Panel.OffsetX - cardHalfW} {-cardTop - timeCfg.Panel.Height}",
                        OffsetMax =
                            $"{timeCfg.Panel.OffsetX + cardHalfW} {-cardTop}",
                    },
                },
                "QuickMenu_Scroll",
                cardName
            );

            // Заголовок карточки
            int titleHalfH = timeCfg.TitleHeight / 2;
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = Msg("QM_GROUP_TIME", player.UserIDString),
                        FontSize = timeCfg.TitleFontSize,
                        Font = "robotocondensed-bold.ttf",
                        Align = TextAnchor.MiddleLeft,
                        Color = timeCfg.TitleColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0.5",
                        AnchorMax = "0 0.5",
                        OffsetMin = $"{timeCfg.TitleOffsetX} {timeCfg.TitleOffsetY - titleHalfH}",
                        OffsetMax =
                            $"{timeCfg.TitleOffsetX + timeCfg.TitleWidth} {timeCfg.TitleOffsetY + titleHalfH}",
                    },
                },
                cardName,
                "QM_Time_Title"
            );

            // Текущее серверное время (обновляется в реальном времени каждую секунду)
            float curServerTime = ConVar.Env.time;
            string formattedCurrentTime = FormatTimeHours(curServerTime);
            int curTimeHalfH = timeCfg.CurrentTime.Height / 2;

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = string.Format(
                            Msg("QM_TIME_CURRENT", player.UserIDString),
                            formattedCurrentTime
                        ),
                        FontSize = timeCfg.CurrentTime.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Align = TextAnchor.MiddleRight,
                        Color = timeCfg.CurrentTime.TextColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "1 0.5",
                        AnchorMax = "1 0.5",
                        OffsetMin =
                            $"{timeCfg.CurrentTime.OffsetX - timeCfg.CurrentTime.Width} {timeCfg.CurrentTime.OffsetY - curTimeHalfH}",
                        OffsetMax =
                            $"{timeCfg.CurrentTime.OffsetX} {timeCfg.CurrentTime.OffsetY + curTimeHalfH}",
                    },
                },
                cardName,
                "QM_Time_Current"
            );

            // Кнопка День (12:00) на позиции первой колонки
            int btnHalfW = timeCfg.Presets.Width / 2;
            int btnHalfH = timeCfg.Presets.Height / 2;
            int stepX = timeCfg.Presets.Width + timeCfg.Presets.SpacingX;
            int col0X = -stepX;

            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.qm time_day",
                        Color = timeCfg.Presets.BackgroundColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = $"{col0X - btnHalfW} {timeCfg.Presets.OffsetY - btnHalfH}",
                        OffsetMax = $"{col0X + btnHalfW} {timeCfg.Presets.OffsetY + btnHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("QM_TIME_DAY", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = timeCfg.Presets.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = timeCfg.Presets.TextColor,
                    },
                },
                cardName,
                "QM_Time_PresetDay"
            );

            // Кнопка Ночь (00:00) на позиции второй колонки
            int col1X = 0;
            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.qm time_night",
                        Color = timeCfg.Presets.BackgroundColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = $"{col1X - btnHalfW} {timeCfg.Presets.OffsetY - btnHalfH}",
                        OffsetMax = $"{col1X + btnHalfW} {timeCfg.Presets.OffsetY + btnHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("QM_TIME_NIGHT", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = timeCfg.Presets.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = timeCfg.Presets.TextColor,
                    },
                },
                cardName,
                "QM_Time_PresetNight"
            );

            // Блок ввода и кнопка применить на позиции третьей колонки
            int col2X = timeCfg.Input.OffsetX;
            int totalInputBlockW =
                timeCfg.Input.Width + timeCfg.ApplyButton.SpacingX + timeCfg.ApplyButton.Width;
            int inputStartX = col2X - totalInputBlockW / 2;

            int inputCenterX = inputStartX + timeCfg.Input.Width / 2;
            int inputHalfW = timeCfg.Input.Width / 2;
            int inputHalfH = timeCfg.Input.Height / 2;
            string inputPanelLayer = "QM_Time_Input_Bg";

            container.Add(
                new CuiPanel
                {
                    Image = { Color = timeCfg.Input.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{inputCenterX - inputHalfW} {timeCfg.Presets.OffsetY - inputHalfH}",
                        OffsetMax =
                            $"{inputCenterX + inputHalfW} {timeCfg.Presets.OffsetY + inputHalfH}",
                    },
                },
                cardName,
                inputPanelLayer
            );

            // CHANGE: Интерактивный плейсхолдер времени: исчезает при клике и переходе в режим ввода
            bool showTimePlaceholder =
                string.IsNullOrEmpty(session.TimeInput) && session.FocusedInput != "time_input";

            if (showTimePlaceholder)
            {
                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = "radminmenu.focus time_input",
                            Color = "0 0 0 0",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = $"{timeCfg.Input.PaddingX} {timeCfg.Input.PaddingY}",
                            OffsetMax = $"{-timeCfg.Input.PaddingX} {-timeCfg.Input.PaddingY}",
                        },
                        Text =
                        {
                            Text = Msg("QM_TIME_PLACEHOLDER", player.UserIDString),
                            FontSize = timeCfg.Input.FontSize,
                            Font = "robotocondensed-regular.ttf",
                            Align = TextAnchor.MiddleCenter,
                            Color = timeCfg.Input.PlaceholderColor,
                        },
                    },
                    inputPanelLayer,
                    "QM_Time_PlaceholderBtn"
                );
            }
            else
            {
                container.Add(
                    new CuiElement
                    {
                        Parent = inputPanelLayer,
                        Name = "QM_Time_InputField",
                        Components =
                        {
                            new CuiInputFieldComponent
                            {
                                Text = session.TimeInput ?? "",
                                Command = "radminmenu.qm_time_input ",
                                FontSize = timeCfg.Input.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Align = TextAnchor.MiddleCenter,
                                Color = timeCfg.Input.TextColor,
                                CharsLimit = timeCfg.Input.CharsLimit,
                                NeedsKeyboard = true,
                                Autofocus = (session.FocusedInput == "time_input"),
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = $"{timeCfg.Input.PaddingX} {timeCfg.Input.PaddingY}",
                                OffsetMax = $"{-timeCfg.Input.PaddingX} {-timeCfg.Input.PaddingY}",
                            },
                        },
                    }
                );
            }

            int applyCenterX =
                inputStartX
                + timeCfg.Input.Width
                + timeCfg.ApplyButton.SpacingX
                + timeCfg.ApplyButton.Width / 2;
            int applyHalfW = timeCfg.ApplyButton.Width / 2;
            int applyHalfH = timeCfg.ApplyButton.Height / 2;

            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.qm_time_apply",
                        Color = timeCfg.ApplyButton.BackgroundColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{applyCenterX - applyHalfW} {timeCfg.Presets.OffsetY - applyHalfH}",
                        OffsetMax =
                            $"{applyCenterX + applyHalfW} {timeCfg.Presets.OffsetY + applyHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("QM_TIME_APPLY", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = timeCfg.ApplyButton.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = timeCfg.ApplyButton.TextColor,
                    },
                },
                cardName,
                "QM_Time_Apply"
            );
        }

        // CHANGE: Отрисовка карточки управления погодой со всеми кнопками быстрых пресетов и переходом в детальные настройки
        private void RenderQuickMenuWeatherCard(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session,
            float cardTop
        )
        {
            var weatherCfg = _config.QuickMenu.WeatherCard;
            string cardName = "QM_WeatherPanel";

            int cardHalfW = weatherCfg.Panel.Width / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = weatherCfg.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 1",
                        AnchorMax = "0.5 1",
                        OffsetMin =
                            $"{weatherCfg.Panel.OffsetX - cardHalfW} {-cardTop - weatherCfg.Panel.Height}",
                        OffsetMax =
                            $"{weatherCfg.Panel.OffsetX + cardHalfW} {-cardTop}",
                    },
                },
                "QuickMenu_Scroll",
                cardName
            );

            // Заголовок карточки
            int titleHalfH = weatherCfg.TitleHeight / 2;
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = Msg("QM_GROUP_WEATHER", player.UserIDString),
                        FontSize = weatherCfg.TitleFontSize,
                        Font = "robotocondensed-bold.ttf",
                        Align = TextAnchor.MiddleLeft,
                        Color = weatherCfg.TitleColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0.5",
                        AnchorMax = "0 0.5",
                        OffsetMin = $"{weatherCfg.TitleOffsetX} {weatherCfg.TitleOffsetY - titleHalfH}",
                        OffsetMax =
                            $"{weatherCfg.TitleOffsetX + weatherCfg.TitleWidth} {weatherCfg.TitleOffsetY + titleHalfH}",
                    },
                },
                cardName,
                "QM_Weather_Title"
            );

            // Кнопка детальных настроек (⚙ НАСТРОЙКИ)
            int detailHalfH = weatherCfg.DetailButton.Height / 2;
            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.nav weather",
                        Color = weatherCfg.DetailButton.BackgroundColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "1 0.5",
                        AnchorMax = "1 0.5",
                        OffsetMin =
                            $"{weatherCfg.DetailButton.OffsetX - weatherCfg.DetailButton.Width} {weatherCfg.DetailButton.OffsetY - detailHalfH}",
                        OffsetMax =
                            $"{weatherCfg.DetailButton.OffsetX} {weatherCfg.DetailButton.OffsetY + detailHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("QM_WEATHER_DETAILS", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = weatherCfg.DetailButton.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = weatherCfg.DetailButton.TextColor,
                    },
                },
                cardName,
                "QM_Weather_DetailBtn"
            );

            // CHANGE: Кнопка "Отчет" удалена из управления погодой; осталось 5 кнопок
            var btns = new List<(string label, string cmd, string color)>
            {
                (
                    Msg("QM_WEATHER_CLEAR", player.UserIDString),
                    "radminmenu.qm weather_clear",
                    weatherCfg.Buttons.ClearColor
                ),
                (
                    Msg("QM_WEATHER_RAIN", player.UserIDString),
                    "radminmenu.qm weather_rain",
                    weatherCfg.Buttons.RainColor
                ),
                (
                    Msg("QM_WEATHER_FOG", player.UserIDString),
                    "radminmenu.qm weather_fog",
                    weatherCfg.Buttons.FogColor
                ),
                (
                    Msg("QM_WEATHER_STORM", player.UserIDString),
                    "radminmenu.qm weather_storm",
                    weatherCfg.Buttons.StormColor
                ),
                (
                    Msg("QM_WEATHER_RESET", player.UserIDString),
                    "radminmenu.qm weather_reset",
                    weatherCfg.Buttons.ResetColor
                ),
            };

            // CHANGE: Ряд кнопок погоды — горизонтальный ScrollView внутри карточки (как в остальных карточках)
            int btnHalfH = weatherCfg.Buttons.Height / 2;
            const int sidePadding = 16;
            int scrollW = weatherCfg.Panel.Width - sidePadding * 2;
            int totalBtnsW =
                (btns.Count * weatherCfg.Buttons.Width)
                + ((btns.Count - 1) * weatherCfg.Buttons.SpacingX);
            string btnScrollName = $"{cardName}_BtnScroll";

            container.Add(
                new CuiElement
                {
                    Name = btnScrollName,
                    Parent = cardName,
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin = $"{sidePadding} {weatherCfg.Buttons.OffsetY - btnHalfH}",
                            OffsetMax = $"{sidePadding + scrollW} {weatherCfg.Buttons.OffsetY + btnHalfH}",
                        },
                        new CuiImageComponent { Color = "0 0 0 0" },
                        new CuiRectMask2DComponent(),
                        new CuiScrollViewComponent
                        {
                            Vertical = false,
                            Horizontal = true,
                            Inertia = true,
                            MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                            ScrollSensitivity = SCROLL_SENSITIVITY,
                            ContentTransform = new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 1",
                                OffsetMin = "0 0",
                                OffsetMax = $"{totalBtnsW} 0",
                            },
                        },
                    },
                }
            );

            for (int i = 0; i < btns.Count; i++)
            {
                var b = btns[i];
                int btnX = i * (weatherCfg.Buttons.Width + weatherCfg.Buttons.SpacingX);

                container.Add(
                    new CuiButton
                    {
                        Button = { Command = b.cmd, Color = b.color },
                        RectTransform =
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin = $"{btnX} {-btnHalfH}",
                            OffsetMax = $"{btnX + weatherCfg.Buttons.Width} {btnHalfH}",
                        },
                        Text =
                        {
                            Text = b.label,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = weatherCfg.Buttons.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = weatherCfg.Buttons.TextColor,
                        },
                    },
                    btnScrollName,
                    $"{cardName}_Btn_{i}"
                );
            }
        }

        // CHANGE: Отрисовка страницы детального управления погодой (WeatherManager)
        private void RenderWeatherManagerContent(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var wCfg = _config.WeatherManager;

            // CHANGE: Удалена кнопка WEATHER_BACK (Weather_BackBtn)
            // 2. Табы категорий погоды
            var tabs = new List<(string key, string langKey)>
            {
                ("presets", "WEATHER_TAB_PRESETS"),
                ("gameplay", "WEATHER_TAB_GAMEPLAY"),
                ("chances", "WEATHER_TAB_CHANCES"),
                ("weather", "WEATHER_TAB_WEATHER"),
                ("atmosphere", "WEATHER_TAB_ATMOSPHERE"),
                ("clouds", "WEATHER_TAB_CLOUDS"),
            };

            int tabsHalfW = wCfg.Tabs.Panel.Width / 2;
            int tabsHalfH = wCfg.Tabs.Panel.Height / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{wCfg.Tabs.Panel.OffsetX - tabsHalfW} {wCfg.Tabs.Panel.OffsetY - tabsHalfH}",
                        OffsetMax =
                            $"{wCfg.Tabs.Panel.OffsetX + tabsHalfW} {wCfg.Tabs.Panel.OffsetY + tabsHalfH}",
                    },
                },
                LayerContentBody,
                "Weather_Tabs"
            );

            int tabStepX = wCfg.Tabs.TabWidth + wCfg.Tabs.TabSpacingX;
            int totalTabsW = (tabs.Count * wCfg.Tabs.TabWidth) + ((tabs.Count - 1) * wCfg.Tabs.TabSpacingX);
            int startTabX = -(totalTabsW / 2) + (wCfg.Tabs.TabWidth / 2);
            int tabHalfW = wCfg.Tabs.TabWidth / 2;
            int tabHalfH = wCfg.Tabs.TabHeight / 2;

            for (int i = 0; i < tabs.Count; i++)
            {
                var t = tabs[i];
                bool isTabActive = session.WeatherSubCategory == t.key;
                string tColor = isTabActive ? wCfg.Tabs.ActiveColor : wCfg.Tabs.InactiveColor;
                int tabX = startTabX + (i * tabStepX);

                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = $"radminmenu.weather_tab {t.key}",
                            Color = tColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{tabX - tabHalfW} {-tabHalfH}",
                            OffsetMax = $"{tabX + tabHalfW} {tabHalfH}",
                        },
                        Text =
                        {
                            Text = Msg(t.langKey, player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = wCfg.Tabs.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = wCfg.Tabs.TextColor,
                        },
                    },
                    "Weather_Tabs",
                    $"Weather_Tab_{t.key}"
                );
            }

            // 3. Содержимое подкатегории
            if (session.WeatherSubCategory == "presets")
            {
                RenderWeatherPresetsGrid(container, player, session);
            }
            else
            {
                RenderWeatherParametersList(container, player, session);
            }
        }

        // CHANGE: Отрисовка сетки пресетов погоды 3x3
        private void RenderWeatherPresetsGrid(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var pCfg = _config.WeatherManager.PresetsGrid;

            var presets = new List<(string key, string titleKey, string descKey, string bg, string cmd)>
            {
                ("Clear", "WEATHER_PRESET_CLEAR", "WEATHER_PRESET_CLEAR_DESC", pCfg.CardBackgroundColor, "radminmenu.weather_preset Clear"),
                ("Dust", "WEATHER_PRESET_DUST", "WEATHER_PRESET_DUST_DESC", pCfg.CardBackgroundColor, "radminmenu.weather_preset Dust"),
                ("Fog", "WEATHER_PRESET_FOG", "WEATHER_PRESET_FOG_DESC", pCfg.CardBackgroundColor, "radminmenu.weather_preset Fog"),
                ("Overcast", "WEATHER_PRESET_OVERCAST", "WEATHER_PRESET_OVERCAST_DESC", pCfg.CardBackgroundColor, "radminmenu.weather_preset Overcast"),
                ("RainHeavy", "WEATHER_PRESET_RAINHEAVY", "WEATHER_PRESET_RAINHEAVY_DESC", pCfg.CardBackgroundColor, "radminmenu.weather_preset RainHeavy"),
                ("RainMild", "WEATHER_PRESET_RAINMILD", "WEATHER_PRESET_RAINMILD_DESC", pCfg.CardBackgroundColor, "radminmenu.weather_preset RainMild"),
                ("Storm", "WEATHER_PRESET_STORM", "WEATHER_PRESET_STORM_DESC", pCfg.CardBackgroundColor, "radminmenu.weather_preset Storm"),
                ("Reset", "WEATHER_PRESET_RESET", "WEATHER_PRESET_RESET_DESC", pCfg.ResetBackgroundColor, "radminmenu.weather_preset Reset"),
            };

            int cols = pCfg.Columns;
            int totalGridW = (cols * pCfg.CardWidth) + ((cols - 1) * pCfg.SpacingX);
            int startX = -(totalGridW / 2) + (pCfg.CardWidth / 2);
            int cardHalfW = pCfg.CardWidth / 2;
            int cardHalfH = pCfg.CardHeight / 2;
            int stepX = pCfg.CardWidth + pCfg.SpacingX;
            int stepY = pCfg.CardHeight + pCfg.SpacingY;
            int startGridY = 110;

            for (int i = 0; i < presets.Count; i++)
            {
                var p = presets[i];
                int col = i % cols;
                int row = i / cols;

                int posX = startX + (col * stepX);
                int posY = startGridY - (row * stepY);
                string cardName = $"Weather_Preset_{p.key}";

                container.Add(
                    new CuiButton
                    {
                        Button = { Command = p.cmd, Color = p.bg },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{posX - cardHalfW} {posY - cardHalfH}",
                            OffsetMax = $"{posX + cardHalfW} {posY + cardHalfH}",
                        },
                        Text = { Text = "" },
                    },
                    LayerContentBody,
                    cardName
                );

                // Заголовок пресета
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Msg(p.titleKey, player.UserIDString),
                            FontSize = pCfg.TitleFontSize,
                            Font = "robotocondensed-bold.ttf",
                            Align = TextAnchor.MiddleCenter,
                            Color = pCfg.TitleColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0.45",
                            AnchorMax = "1 1",
                            OffsetMin = "0 0",
                            OffsetMax = "0 0",
                        },
                    },
                    cardName
                );

                // Описание / команда пресета
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Msg(p.descKey, player.UserIDString),
                            FontSize = pCfg.SubtitleFontSize,
                            Font = "robotocondensed-regular.ttf",
                            Align = TextAnchor.MiddleCenter,
                            Color = pCfg.SubtitleColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 0.45",
                            OffsetMin = "0 0",
                            OffsetMax = "0 0",
                        },
                    },
                    cardName
                );
            }
        }

        // CHANGE: Отрисовка списка параметров погоды со скроллом, быстрыми кнопками и полями ввода
        private void RenderWeatherParametersList(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var wCfg = _config.WeatherManager;
            string[] paramList;

            switch (session.WeatherSubCategory)
            {
                case "gameplay":
                    paramList = new[] { "weather.wetness_rain", "weather.wetness_snow" };
                    break;
                case "chances":
                    paramList = new[]
                    {
                        "weather.clear_chance",
                        "weather.dust_chance",
                        "weather.fog_chance",
                        "weather.overcast_chance",
                        "weather.storm_chance",
                        "weather.rain_chance",
                    };
                    break;
                case "weather":
                    paramList = new[]
                    {
                        "weather.rain",
                        "weather.wind",
                        "weather.thunder",
                        "weather.rainbow",
                        "weather.fog",
                    };
                    break;
                case "atmosphere":
                    paramList = new[]
                    {
                        "weather.atmosphere_rayleigh",
                        "weather.atmosphere_mie",
                        "weather.atmosphere_brightness",
                        "weather.atmosphere_contrast",
                        "weather.atmosphere_directionality",
                    };
                    break;
                case "clouds":
                    paramList = new[]
                    {
                        "weather.cloud_size",
                        "weather.cloud_opacity",
                        "weather.cloud_coverage",
                        "weather.cloud_sharpness",
                        "weather.cloud_coloring",
                        "weather.cloud_attenuation",
                        "weather.cloud_scattering",
                        "weather.cloud_brightness",
                    };
                    break;
                default:
                    paramList = new string[0];
                    break;
            }

            const int weatherPaddingTop = 6;
            const int weatherPaddingBottom = 6;
            const int weatherSpacingY = 8;
            const int weatherScrollHalfW = 330;
            const int weatherScrollHalfH = 175;
            float weatherViewHeight = 350f;

            float totalHeight = Mathf.Max(
                weatherViewHeight,
                weatherPaddingTop
                    + weatherPaddingBottom
                    + (paramList.Length * wCfg.Row.Height)
                    + (Mathf.Max(0, paramList.Length - 1) * weatherSpacingY)
            );

            container.Add(
                new CuiElement
                {
                    Parent = LayerContentBody,
                    Name = "Weather_Scroll",
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{-weatherScrollHalfW} {-30 - weatherScrollHalfH}",
                            OffsetMax = $"{weatherScrollHalfW} {-30 + weatherScrollHalfH}",
                        },
                        new CuiImageComponent
                        {
                            Color = "0 0 0 0",
                        },
                        new CuiRectMask2DComponent(),
                        new CuiScrollViewComponent
                        {
                            Vertical = true,
                            Horizontal = false,
                            Inertia = true,
                            MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                            ScrollSensitivity = SCROLL_SENSITIVITY,
                            ContentTransform = new CuiRectTransformComponent
                            {
                                AnchorMin = "0 1",
                                AnchorMax = "1 1",
                                OffsetMin = $"0 {-totalHeight}",
                                OffsetMax = "0 0",
                            },
                        },
                    },
                }
            );

            int rowHalfW = wCfg.Row.Width / 2;

            for (int i = 0; i < paramList.Length; i++)
            {
                string convar = paramList[i];
                float rowTop =
                    weatherPaddingTop + (i * (wCfg.Row.Height + weatherSpacingY));
                string rowName = $"Weather_Row_{convar}";

                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = wCfg.Row.BackgroundColor },
                        RectTransform =
                        {
                            AnchorMin = "0.5 1",
                            AnchorMax = "0.5 1",
                            OffsetMin = $"{-rowHalfW} {-rowTop - wCfg.Row.Height}",
                            OffsetMax = $"{rowHalfW} {-rowTop}",
                        },
                    },
                    "Weather_Scroll",
                    rowName
                );

                // Название параметра
                string paramTitle = Msg($"PARAM_{convar}", player.UserIDString);
                int labelHalfH = wCfg.Row.LabelHeight / 2;
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = !string.IsNullOrEmpty(paramTitle) ? paramTitle : convar,
                            FontSize = wCfg.Row.TitleFontSize,
                            Font = "robotocondensed-bold.ttf",
                            Align = TextAnchor.MiddleLeft,
                            Color = wCfg.Row.TitleColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin =
                                $"{wCfg.Row.LabelOffsetX} {wCfg.Row.LabelOffsetY - labelHalfH}",
                            OffsetMax =
                                $"{wCfg.Row.LabelOffsetX + wCfg.Row.LabelWidth} {wCfg.Row.LabelOffsetY + labelHalfH}",
                        },
                    },
                    rowName
                );

                // ConVar имя
                int convarHalfH = wCfg.Row.ConVarHeight / 2;
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = convar,
                            FontSize = wCfg.Row.ConVarFontSize,
                            Font = "robotocondensed-regular.ttf",
                            Align = TextAnchor.MiddleLeft,
                            Color = wCfg.Row.ConVarColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin =
                                $"{wCfg.Row.ConVarOffsetX} {wCfg.Row.ConVarOffsetY - convarHalfH}",
                            OffsetMax =
                                $"{wCfg.Row.ConVarOffsetX + wCfg.Row.ConVarWidth} {wCfg.Row.ConVarOffsetY + convarHalfH}",
                        },
                    },
                    rowName
                );

                // Быстрые кнопки: Авто (-1), 0.0, 0.5, 1.0
                var quickSteps = new[]
                {
                    (Msg("WEATHER_PARAM_AUTO", player.UserIDString), "-1", wCfg.QuickButtons.AutoButtonColor),
                    ("0.0", "0", wCfg.QuickButtons.BackgroundColor),
                    ("0.5", "0.5", wCfg.QuickButtons.BackgroundColor),
                    ("1.0", "1", wCfg.QuickButtons.BackgroundColor),
                };

                int qBtnHalfW = wCfg.QuickButtons.ButtonWidth / 2;
                int qBtnHalfH = wCfg.QuickButtons.ButtonHeight / 2;
                int qStepX = wCfg.QuickButtons.ButtonWidth + wCfg.QuickButtons.SpacingX;

                for (int qi = 0; qi < quickSteps.Length; qi++)
                {
                    var qs = quickSteps[qi];
                    int qX = wCfg.QuickButtons.OffsetX + (qi * qStepX);

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = $"radminmenu.weather_set {convar} {qs.Item2}",
                                Color = qs.Item3,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0.5",
                                AnchorMax = "0 0.5",
                                OffsetMin =
                                    $"{qX} {wCfg.QuickButtons.OffsetY - qBtnHalfH}",
                                OffsetMax =
                                    $"{qX + wCfg.QuickButtons.ButtonWidth} {wCfg.QuickButtons.OffsetY + qBtnHalfH}",
                            },
                            Text =
                            {
                                Text = qs.Item1,
                                Align = TextAnchor.MiddleCenter,
                                FontSize = wCfg.QuickButtons.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = wCfg.QuickButtons.TextColor,
                            },
                        },
                        rowName
                    );
                }

                // Поле числового ввода
                int inpHalfH = wCfg.Input.Height / 2;
                string inputBgLayer = $"{rowName}_InputBg";

                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = wCfg.Input.BackgroundColor },
                        RectTransform =
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin =
                                $"{wCfg.Input.OffsetX} {wCfg.Input.OffsetY - inpHalfH}",
                            OffsetMax =
                                $"{wCfg.Input.OffsetX + wCfg.Input.Width} {wCfg.Input.OffsetY + inpHalfH}",
                        },
                    },
                    rowName,
                    inputBgLayer
                );

                string currentValue = session.WeatherInputs.TryGetValue(convar, out string storedVal)
                    ? storedVal
                    : GetWeatherConVarValue(convar);

                container.Add(
                    new CuiElement
                    {
                        Parent = inputBgLayer,
                        Name = $"{rowName}_InputField",
                        Components =
                        {
                            new CuiInputFieldComponent
                            {
                                Text = currentValue ?? "",
                                Command = $"radminmenu.weather_input {convar} ",
                                FontSize = wCfg.Input.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Align = TextAnchor.MiddleCenter,
                                Color = wCfg.Input.TextColor,
                                CharsLimit = wCfg.Input.CharsLimit,
                                NeedsKeyboard = true,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = $"{wCfg.Input.PaddingX} {wCfg.Input.PaddingY}",
                                OffsetMax = $"{-wCfg.Input.PaddingX} {-wCfg.Input.PaddingY}",
                            },
                        },
                    }
                );

                // Кнопка Применить
                int applyHalfH = wCfg.ApplyButton.Height / 2;
                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = $"radminmenu.weather_apply {convar}",
                            Color = wCfg.ApplyButton.BackgroundColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin =
                                $"{wCfg.ApplyButton.OffsetX} {wCfg.ApplyButton.OffsetY - applyHalfH}",
                            OffsetMax =
                                $"{wCfg.ApplyButton.OffsetX + wCfg.ApplyButton.Width} {wCfg.ApplyButton.OffsetY + applyHalfH}",
                        },
                        Text =
                        {
                            Text = Msg("WEATHER_PARAM_APPLY", player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = wCfg.ApplyButton.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = wCfg.ApplyButton.TextColor,
                        },
                    },
                    rowName
                );
            }
        }

        // CHANGE: Точечное получение текущего значения ConVar погоды через безопасный типизированный свитч
        private string GetWeatherConVarValue(string conVarName)
        {
            try
            {
                switch (conVarName)
                {
                    case "weather.wetness_rain":
                        return ConVar.Weather.wetness_rain.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.wetness_snow":
                        return ConVar.Weather.wetness_snow.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.clear_chance":
                        return ConVar.Weather.clear_chance.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.dust_chance":
                        return ConVar.Weather.dust_chance.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.fog_chance":
                        return ConVar.Weather.fog_chance.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.overcast_chance":
                        return ConVar.Weather.overcast_chance.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.storm_chance":
                        return ConVar.Weather.storm_chance.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.rain_chance":
                        return ConVar.Weather.rain_chance.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.rain":
                        return ConVar.Weather.rain.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.wind":
                        return ConVar.Weather.wind.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.thunder":
                        return ConVar.Weather.thunder.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.rainbow":
                        return ConVar.Weather.rainbow.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.fog":
                        return ConVar.Weather.fog.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.atmosphere_rayleigh":
                        return ConVar.Weather.atmosphere_rayleigh.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.atmosphere_mie":
                        return ConVar.Weather.atmosphere_mie.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.atmosphere_brightness":
                        return ConVar.Weather.atmosphere_brightness.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.atmosphere_contrast":
                        return ConVar.Weather.atmosphere_contrast.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.atmosphere_directionality":
                        return ConVar.Weather.atmosphere_directionality.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.cloud_size":
                        return ConVar.Weather.cloud_size.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.cloud_opacity":
                        return ConVar.Weather.cloud_opacity.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.cloud_coverage":
                        return ConVar.Weather.cloud_coverage.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.cloud_sharpness":
                        return ConVar.Weather.cloud_sharpness.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.cloud_coloring":
                        return ConVar.Weather.cloud_coloring.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.cloud_attenuation":
                        return ConVar.Weather.cloud_attenuation.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.cloud_scattering":
                        return ConVar.Weather.cloud_scattering.ToString("0.##", CultureInfo.InvariantCulture);
                    case "weather.cloud_brightness":
                        return ConVar.Weather.cloud_brightness.ToString("0.##", CultureInfo.InvariantCulture);
                }
            }
            catch { }

            return "-1";
        }

        // CHANGE: Точечное обновление виджета серверного времени в реальном времени
        private void UpdateQuickMenuTimeOnly(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            AdminSession session = GetSession(player);
            if (session == null || session.CurrentCategory != "quickmenu")
                return;

            CuiHelper.DestroyUi(player, "QM_Time_Current");

            var timeCfg = _config.QuickMenu.TimeCard;
            var container = new CuiElementContainer();

            float curServerTime = ConVar.Env.time;
            string formattedCurrentTime = FormatTimeHours(curServerTime);
            int curTimeHalfH = timeCfg.CurrentTime.Height / 2;

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = string.Format(
                            Msg("QM_TIME_CURRENT", player.UserIDString),
                            formattedCurrentTime
                        ),
                        FontSize = timeCfg.CurrentTime.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Align = TextAnchor.MiddleRight,
                        Color = timeCfg.CurrentTime.TextColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "1 0.5",
                        AnchorMax = "1 0.5",
                        OffsetMin =
                            $"{timeCfg.CurrentTime.OffsetX - timeCfg.CurrentTime.Width} {timeCfg.CurrentTime.OffsetY - curTimeHalfH}",
                        OffsetMax =
                            $"{timeCfg.CurrentTime.OffsetX} {timeCfg.CurrentTime.OffsetY + curTimeHalfH}",
                    },
                },
                "QM_TimePanel",
                "QM_Time_Current"
            );

            CuiHelper.AddUi(player, container);
        }

        // CHANGE: Форматирование времени суток (в часах от 0.0 до 24.0) в строковый формат ЧЧ:ММ
        private string FormatTimeHours(float timeInHours)
        {
            timeInHours = ((timeInHours % 24f) + 24f) % 24f;
            int totalMinutes = Mathf.RoundToInt(timeInHours * 60f) % 1440;
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return $"{hours:D2}:{minutes:D2}";
        }

        #endregion

        #region Content Page: Player List

        // CHANGE: Надежная детекция онлайн-статуса игрока
        private bool IsPlayerOnline(IPlayer player)
        {
            if (player == null)
                return false;

            if (ulong.TryParse(player.Id, out ulong uid))
            {
                BasePlayer bp = BasePlayer.FindByID(uid);
                if (bp != null && bp.IsConnected)
                    return true;
            }

            return player.IsConnected;
        }

        // CHANGE: Надежная детекция спящего игрока на сервере
        private bool IsPlayerSleeping(IPlayer player)
        {
            if (player == null || IsPlayerOnline(player))
                return false;

            if (ulong.TryParse(player.Id, out ulong uid))
            {
                return BasePlayer.FindSleeping(uid) != null;
            }

            return BasePlayer.FindSleeping(player.Id) != null;
        }

        private void RenderPlayerListContent(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var plCfg = _config.PlayerList;

            // 1. Фильтры
            var filters = new List<(string key, string langKey)>
            {
                ("online", "FILTER_ONLINE"),
                ("offline", "FILTER_OFFLINE"),
                ("sleeping", "FILTER_SLEEPING"),
                ("admins", "FILTER_ADMINS"),
                ("mods", "FILTER_MODS"),
                ("all", "FILTER_ALL"),
            };

            int totalFilterW =
                filters.Count * plCfg.Filters.Width + (filters.Count - 1) * plCfg.Filters.Spacing;
            int startFilterX = -totalFilterW / 2 + plCfg.Filters.Width / 2;
            int filterHalfW = plCfg.Filters.Width / 2;
            int filterHalfH = plCfg.Filters.Height / 2;

            for (int i = 0; i < filters.Count; i++)
            {
                var (fKey, langKey) = filters[i];
                bool isActive = (session.PlayerFilter == fKey);
                string fColor = isActive ? plCfg.Filters.ActiveColor : plCfg.Filters.InactiveColor;
                int fx = startFilterX + i * (plCfg.Filters.Width + plCfg.Filters.Spacing);

                container.Add(
                    new CuiButton
                    {
                        Button = { Command = $"radminmenu.pl_filter {fKey}", Color = fColor },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{fx - filterHalfW} {plCfg.Filters.OffsetY - filterHalfH}",
                            OffsetMax = $"{fx + filterHalfW} {plCfg.Filters.OffsetY + filterHalfH}",
                        },
                        Text =
                        {
                            Text = Msg(langKey, player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = plCfg.Filters.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = plCfg.Filters.TextColor,
                        },
                    },
                    LayerContentBody,
                    $"PlFilter_{fKey}"
                );
            }

            // 2. Панель поиска
            int searchHalfW = plCfg.Search.Width / 2;
            int searchHalfH = plCfg.Search.Height / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = plCfg.Search.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = $"{-searchHalfW} {plCfg.Search.OffsetY - searchHalfH}",
                        OffsetMax = $"{searchHalfW} {plCfg.Search.OffsetY + searchHalfH}",
                    },
                },
                LayerContentBody,
                "PlSearchBg"
            );

            // CHANGE: Интерактивный плейсхолдер поиска игроков: отображается когда пусто, исчезает по клику
            bool showPlSearchPlaceholder =
                string.IsNullOrEmpty(session.PlayerSearch) && session.FocusedInput != "pl_search";

            if (showPlSearchPlaceholder)
            {
                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = "radminmenu.focus pl_search",
                            Color = "0 0 0 0",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = $"{plCfg.Search.PaddingX} 0",
                            OffsetMax = $"{-plCfg.Search.PaddingX} 0",
                        },
                        Text =
                        {
                            Text = Msg("PL_SEARCH_PLACEHOLDER", player.UserIDString),
                            FontSize = plCfg.Search.FontSize,
                            Font = "robotocondensed-regular.ttf",
                            Align = TextAnchor.MiddleLeft,
                            Color = plCfg.Search.PlaceholderColor,
                        },
                    },
                    "PlSearchBg",
                    "PlSearchPlaceholderBtn"
                );
            }
            else
            {
                container.Add(
                    new CuiElement
                    {
                        Parent = "PlSearchBg",
                        Name = "PlSearchInput",
                        Components =
                        {
                            new CuiInputFieldComponent
                            {
                                Text = session.PlayerSearch,
                                Command = "radminmenu.pl_search ",
                                Align = TextAnchor.MiddleLeft,
                                FontSize = plCfg.Search.FontSize,
                                Font = "robotocondensed-regular.ttf",
                                Color = plCfg.Search.TextColor,
                                CharsLimit = 32,
                                NeedsKeyboard = true,
                                Autofocus = (session.FocusedInput == "pl_search"),
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = $"{plCfg.Search.PaddingX} 0",
                                OffsetMax = $"{-plCfg.Search.PaddingX} 0",
                            },
                        },
                    }
                );
            }

            // 3. Получение списка игроков по фильтру и поиску с O(N) сложностью
            List<IPlayer> queryList = new List<IPlayer>();
            HashSet<ulong> onlineIds = new HashSet<ulong>();
            foreach (BasePlayer bp in BasePlayer.activePlayerList)
            {
                if (bp != null && bp.IsConnected)
                    onlineIds.Add(bp.userID);
            }

            HashSet<ulong> sleepingIds = new HashSet<ulong>();
            foreach (BasePlayer bp in BasePlayer.sleepingPlayerList)
            {
                if (bp != null)
                    sleepingIds.Add(bp.userID);
            }

            if (session.PlayerFilter == "online")
            {
                foreach (BasePlayer active in BasePlayer.activePlayerList)
                {
                    if (active != null && active.IsConnected)
                    {
                        IPlayer iPlayer = covalence.Players.FindPlayerById(active.UserIDString);
                        if (iPlayer != null)
                            queryList.Add(iPlayer);
                    }
                }
            }
            else if (session.PlayerFilter == "sleeping")
            {
                foreach (BasePlayer sleeper in BasePlayer.sleepingPlayerList)
                {
                    if (sleeper != null)
                    {
                        IPlayer iPlayer = covalence.Players.FindPlayerById(sleeper.UserIDString);
                        if (iPlayer != null)
                            queryList.Add(iPlayer);
                    }
                }
            }
            else
            {
                HashSet<string> addedIds = new HashSet<string>();
                foreach (IPlayer p in covalence.Players.All)
                {
                    if (p != null)
                    {
                        queryList.Add(p);
                        addedIds.Add(p.Id);
                    }
                }
                foreach (BasePlayer active in BasePlayer.activePlayerList)
                {
                    if (active != null && addedIds.Add(active.UserIDString))
                    {
                        IPlayer iPlayer = covalence.Players.FindPlayerById(active.UserIDString);
                        if (iPlayer != null)
                            queryList.Add(iPlayer);
                    }
                }
                foreach (BasePlayer sleeper in BasePlayer.sleepingPlayerList)
                {
                    if (sleeper != null && addedIds.Add(sleeper.UserIDString))
                    {
                        IPlayer iPlayer = covalence.Players.FindPlayerById(sleeper.UserIDString);
                        if (iPlayer != null)
                            queryList.Add(iPlayer);
                    }
                }

                if (session.PlayerFilter == "offline")
                {
                    queryList = queryList.Where(p =>
                    {
                        if (ulong.TryParse(p.Id, out ulong uid))
                            return !onlineIds.Contains(uid) && !sleepingIds.Contains(uid);
                        return !p.IsConnected;
                    }).ToList();
                }
                else if (session.PlayerFilter == "admins")
                {
                    queryList = queryList.Where(p => p.IsAdmin || permission.UserHasGroup(p.Id, "admin")).ToList();
                }
                else if (session.PlayerFilter == "mods")
                {
                    queryList = queryList.Where(p => permission.UserHasGroup(p.Id, "moderator")).ToList();
                }
            }

            IEnumerable<IPlayer> query = queryList;

            if (!string.IsNullOrWhiteSpace(session.PlayerSearch))
            {
                string s = session.PlayerSearch.Trim();
                query = query.Where(p =>
                    p.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 || p.Id.Contains(s)
                );
            }

            List<IPlayer> filteredPlayers = query
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 4. Отрисовка карточек игроков через ScrollView (без пагинации)
            int cols = plCfg.Card.Columns;
            int rows = Mathf.CeilToInt((float)filteredPlayers.Count / cols);
            if (rows < 1)
                rows = 1;

            float viewHeight = 395f;
            float totalHeight = Mathf.Max(
                viewHeight,
                10f + rows * plCfg.Card.Height + (rows - 1) * plCfg.Card.SpacingY
            );

            int scrollHalfW = 330;
            int scrollHalfH = 197;

            container.Add(
                new CuiElement
                {
                    Parent = LayerContentBody,
                    Name = "PlayerList_Scroll",
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{-scrollHalfW} {-47 - scrollHalfH}",
                            OffsetMax = $"{scrollHalfW} {-47 + scrollHalfH}",
                        },
                        new CuiImageComponent
                        {
                            Color = "0 0 0 0",
                        },
                        new CuiRectMask2DComponent(),
                        new CuiScrollViewComponent
                        {
                            Vertical = true,
                            Horizontal = false,
                            Inertia = true,
                            MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                            ScrollSensitivity = SCROLL_SENSITIVITY,
                            ContentTransform = new CuiRectTransformComponent
                            {
                                AnchorMin = "0 1",
                                AnchorMax = "1 1",
                                OffsetMin = $"0 {-totalHeight}",
                                OffsetMax = "0 0",
                            },
                        },
                    },
                }
            );

            for (int i = 0; i < filteredPlayers.Count; i++)
            {
                var target = filteredPlayers[i];
                int col = i % cols;
                int row = i / cols;

                float xPos = 2f + col * (plCfg.Card.Width + plCfg.Card.SpacingX);
                float yPos = 5f + row * (plCfg.Card.Height + plCfg.Card.SpacingY);

                // CHANGE: Определение статуса игрока через O(1) хэш-таблицы
                ulong targetUid;
                bool hasTargetUid = ulong.TryParse(target.Id, out targetUid);
                bool isOnline = hasTargetUid ? onlineIds.Contains(targetUid) : target.IsConnected;
                bool isSleeping = !isOnline && hasTargetUid && sleepingIds.Contains(targetUid);

                string cardColor =
                    (isOnline || isSleeping)
                        ? plCfg.Card.ActiveCardColor
                        : plCfg.Card.InactiveCardColor;
                string statusColor = isOnline
                    ? plCfg.Card.StatusOnlineColor
                    : (isSleeping ? plCfg.Card.StatusSleepingColor : plCfg.Card.StatusOfflineColor);

                string cardName = $"PlayerCard_{target.Id}";

                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = $"radminmenu.pl_select {target.Id}",
                            Color = cardColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 1",
                            AnchorMax = "0 1",
                            OffsetMin = $"{xPos} {-yPos - plCfg.Card.Height}",
                            OffsetMax = $"{xPos + plCfg.Card.Width} {-yPos}",
                        },
                        Text =
                        {
                            Text = $"  {target.Name}",
                            Align = TextAnchor.MiddleLeft,
                            FontSize = plCfg.Card.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = plCfg.Card.TextColor,
                        },
                    },
                    "PlayerList_Scroll",
                    cardName
                );

                // Индикатор онлайн статуса (полоска слева)
                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = statusColor },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 1",
                            OffsetMin = "0 0",
                            OffsetMax = "3 0",
                        },
                    },
                    cardName
                );
            }
        }

        #endregion

        #region Content Page: User Info

        private void RenderUserInfoContent(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var uiCfg = _config.UserInfo;

            IPlayer target = covalence.Players.FindPlayerById(session.SelectedUserId.ToString());
            if (target == null)
            {
                session.CurrentCategory = "players";
                RenderContent(player);
                return;
            }

            BasePlayer targetBasePlayer = BasePlayer.FindAwakeOrSleeping(target.Id);

            int backHalfW = uiCfg.BackButton.Width / 2;
            int backHalfH = uiCfg.BackButton.Height / 2;

            // CHANGE: Кнопка возврата к списку с настраиваемой позицией над аватаром
            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.nav players",
                        Color = uiCfg.BackButton.BackgroundColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{uiCfg.BackButton.OffsetX - backHalfW} {uiCfg.BackButton.OffsetY - backHalfH}",
                        OffsetMax =
                            $"{uiCfg.BackButton.OffsetX + backHalfW} {uiCfg.BackButton.OffsetY + backHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("UI_BACK", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = uiCfg.BackButton.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = uiCfg.BackButton.TextColor,
                    },
                },
                LayerContentBody,
                "UI_BackBtn"
            );

            // Аватарка
            int avHalf = uiCfg.Avatar.Size / 2;
            container.Add(
                new CuiPanel
                {
                    Image = { Color = uiCfg.Avatar.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{uiCfg.Avatar.OffsetX - avHalf} {uiCfg.Avatar.OffsetY - avHalf}",
                        OffsetMax =
                            $"{uiCfg.Avatar.OffsetX + avHalf} {uiCfg.Avatar.OffsetY + avHalf}",
                    },
                },
                LayerContentBody,
                "UI_AvatarBg"
            );

            if (
                !_config.General.DisablePlayerAvatars
                && _cachedSteamInfo.TryGetValue(session.SelectedUserId, out SteamInfo steamInfo)
                && steamInfo.Avatars?.Length > 2
                && !string.IsNullOrEmpty(steamInfo.Avatars[2])
            )
            {
                container.Add(
                    new CuiElement
                    {
                        Parent = "UI_AvatarBg",
                        Components =
                        {
                            new CuiRawImageComponent
                            {
                                Url = steamInfo.Avatars[2],
                                Color = "1 1 1 1",
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = "0 0",
                                OffsetMax = "0 0",
                            },
                        },
                    }
                );
            }
            else
            {
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = "STEAM\nAVATAR",
                            Align = TextAnchor.MiddleCenter,
                            FontSize = 14,
                            Font = "robotocondensed-bold.ttf",
                            Color = uiCfg.Details.DetailsColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = "0 0",
                            OffsetMax = "0 0",
                        },
                    },
                    "UI_AvatarBg"
                );

                if (
                    !_config.General.DisablePlayerAvatars
                    && !_cachedSteamInfo.ContainsKey(session.SelectedUserId)
                )
                {
                    ulong currentTargetId = session.SelectedUserId;
                    RequestSteamInfo(
                        currentTargetId,
                        () =>
                        {
                            if (
                                session.CurrentCategory == "userinfo"
                                && session.SelectedUserId == currentTargetId
                            )
                                RenderContent(player);
                        }
                    );
                }
            }

            // Блок детальной информации
            int infoHalfW = uiCfg.Details.Width / 2;
            int infoHalfH = uiCfg.Details.Height / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = uiCfg.Details.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{uiCfg.Details.OffsetX - infoHalfW} {uiCfg.Details.OffsetY - infoHalfH}",
                        OffsetMax =
                            $"{uiCfg.Details.OffsetX + infoHalfW} {uiCfg.Details.OffsetY + infoHalfH}",
                    },
                },
                LayerContentBody,
                "UI_InfoPanel"
            );

            // Имя и SteamID
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = $"<b>{target.Name}</b> ({target.Id})",
                        Align = TextAnchor.UpperLeft,
                        FontSize = uiCfg.Details.NameFontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = uiCfg.Details.NameColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin = $"{uiCfg.Details.LeftColumnOffsetMinX} 0",
                        OffsetMax = $"{uiCfg.Details.RightColumnOffsetMaxX} -10",
                    },
                },
                "UI_InfoPanel"
            );

            // Статистика игрока
            float hp = targetBasePlayer != null ? targetBasePlayer.health : 0;
            float maxHp = targetBasePlayer != null ? targetBasePlayer.MaxHealth() : 100;
            float rad = (targetBasePlayer != null && targetBasePlayer.metabolism != null && targetBasePlayer.metabolism.radiation_poison != null)
                ? targetBasePlayer.metabolism.radiation_poison.value
                : 0f;
            string grid =
                targetBasePlayer != null
                    ? MapHelper.PositionToString(targetBasePlayer.transform.position)
                    : "Unknown";

            // CHANGE: IP видят только администраторы (authLevel >= 2)
            bool canViewIp = HasAccess(player);

            string ipAddress =
                targetBasePlayer?.net?.connection?.ipaddress ?? target.Address ?? "N/A";
            int ping = target.Ping;

            string ipPing = canViewIp
                ? Msg("UI_PING_IP", player.UserIDString, ipAddress, ping)
                : "IP: ***.***.***.***";

            // CHANGE: Добавлено отображение радиации игрока
            string detailsLeft =
                $"{Msg("UI_HEALTH", player.UserIDString, (int)hp, (int)maxHp)}\n{Msg("UI_RADIATION", player.UserIDString, (int)rad)}\n{Msg("UI_GRID", player.UserIDString, grid)}\n{ipPing}";

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = detailsLeft,
                        Align = TextAnchor.MiddleLeft,
                        FontSize = uiCfg.Details.DetailsFontSize,
                        Font = "robotocondensed-regular.ttf",
                        Color = uiCfg.Details.DetailsColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "0.5 1",
                        OffsetMin = $"{uiCfg.Details.LeftColumnOffsetMinX} -25",
                        OffsetMax = "0 0",
                    },
                },
                "UI_InfoPanel",
                "UI_Info_DetailsLeft"
            );

            string detailsRight = "";
            if (Economics != null)
            {
                double bal = Economics.Call<double>("Balance", session.SelectedUserId);
                detailsRight += Msg("UI_BALANCE", player.UserIDString, bal) + "\n";
            }
            if (Clans != null)
            {
                string tag = Clans.Call<string>("GetClanOf", target.Id);
                if (!string.IsNullOrEmpty(tag))
                    detailsRight += Msg("UI_CLAN", player.UserIDString, tag) + "\n";
            }
            if (targetBasePlayer != null && targetBasePlayer.IsConnected && targetBasePlayer.Connection != null)
            {
                TimeSpan span = TimeSpan.FromSeconds(
                    targetBasePlayer.Connection.GetSecondsConnected()
                );
                detailsRight +=
                    Msg("UI_CTIME", player.UserIDString, span.Hours, span.Minutes, span.Seconds)
                    + "\n";
            }
            else
            {
                detailsRight += Msg("UI_STATUS_OFFLINE", player.UserIDString) + "\n";
            }

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = detailsRight,
                        Align = TextAnchor.MiddleLeft,
                        FontSize = uiCfg.Details.DetailsFontSize,
                        Font = "robotocondensed-regular.ttf",
                        Color = uiCfg.Details.DetailsColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0",
                        AnchorMax = "1 1",
                        OffsetMin = "0 -25",
                        OffsetMax = $"{uiCfg.Details.RightColumnOffsetMaxX} 0",
                    },
                },
                "UI_InfoPanel",
                "UI_Info_DetailsRight"
            );

            // CHANGE: Кнопки действий над игроком (без наказаний: mute, kick, ban, с кнопкой снятия радиации вместо 50% лечения)
            var actions = new List<(
                string key,
                string langKey,
                string cmd,
                string color,
                int row,
                int col
            )>
            {
                (
                    "tp_self_to",
                    "ACT_TP_SELF_TO",
                    "radminmenu.act tp_self_to",
                    uiCfg.Actions.DefaultButtonColor,
                    0,
                    0
                ),
                (
                    "tp_to_self",
                    "ACT_TP_TO_SELF",
                    "radminmenu.act tp_to_self",
                    uiCfg.Actions.DefaultButtonColor,
                    0,
                    1
                ),
                (
                    "tp_auth",
                    "ACT_TP_AUTH",
                    "radminmenu.act tp_auth",
                    uiCfg.Actions.DefaultButtonColor,
                    0,
                    2
                ),
                (
                    "tp_death",
                    "ACT_TP_DEATH",
                    "radminmenu.act tp_death",
                    uiCfg.Actions.DefaultButtonColor,
                    0,
                    3
                ),
                (
                    "heal_100",
                    "ACT_HEAL_100",
                    "radminmenu.act heal_100",
                    uiCfg.Actions.SuccessButtonColor,
                    1,
                    0
                ),
                (
                    "clear_rad",
                    "ACT_CLEAR_RAD",
                    "radminmenu.act clear_rad",
                    uiCfg.Actions.RadiationButtonColor,
                    1,
                    1
                ),
                (
                    "spectate",
                    "ACT_SPECTATE",
                    "radminmenu.act spectate",
                    uiCfg.Actions.WarningButtonColor,
                    1,
                    2
                ),
                // CHANGE: Кнопка креатив-режима перенесена в Быстрое меню самого админа (запрет выдачи креатива другим игрокам)
                (
                    "cuff",
                    "ACT_CUFF",
                    "radminmenu.act toggle_cuff",
                    targetBasePlayer != null && IsPlayerHandcuffed(targetBasePlayer)
                        ? uiCfg.Actions.ActiveButtonColor
                        : uiCfg.Actions.DefaultButtonColor,
                    2,
                    0
                ),
                (
                    "strip_inv",
                    "ACT_STRIP_INV",
                    "radminmenu.act strip_inv",
                    uiCfg.Actions.WarningButtonColor,
                    2,
                    1
                ),
                (
                    "view_inv",
                    "ACT_VIEW_INV",
                    "radminmenu.act view_inv",
                    targetBasePlayer != null
                        ? uiCfg.Actions.ActiveButtonColor
                        : uiCfg.Actions.DefaultButtonColor,
                    2,
                    2
                ),
                (
                    "view_bp",
                    "ACT_VIEW_BP",
                    "radminmenu.act view_bp",
                    targetBasePlayer != null && GetEquippedBackpack(targetBasePlayer) != null
                        ? uiCfg.Actions.ActiveButtonColor
                        : uiCfg.Actions.DefaultButtonColor,
                    2,
                    3
                ),
                (
                    "unlock_bp",
                    "ACT_UNLOCK_BP",
                    "radminmenu.act unlock_bp",
                    uiCfg.Actions.DefaultButtonColor,
                    3,
                    0
                ),
                (
                    "revoke_bp",
                    "ACT_REVOKE_BP",
                    "radminmenu.act revoke_bp",
                    uiCfg.Actions.WarningButtonColor,
                    3,
                    1
                ),
                // CHANGE: Откат только выданных плагином чертежей (по снапшоту перед "Изучить чертежи")
                (
                    "revoke_bp_granted",
                    "ACT_REVOKE_BP_GRANTED",
                    "radminmenu.act revoke_bp_granted",
                    uiCfg.Actions.WarningButtonColor,
                    3,
                    2
                ),
                ("kill", "ACT_KILL", "radminmenu.act kill", uiCfg.Actions.WarningButtonColor, 3, 3),
            };

            int actHalfW = uiCfg.Actions.Width / 2;
            int actHalfH = uiCfg.Actions.Height / 2;

            foreach (var item in actions)
            {
                int x =
                    uiCfg.Actions.StartX
                    + item.col * (uiCfg.Actions.Width + uiCfg.Actions.SpacingX);
                int y =
                    uiCfg.Actions.StartY
                    - item.row * (uiCfg.Actions.Height + uiCfg.Actions.SpacingY);

                container.Add(
                    new CuiButton
                    {
                        Button = { Command = item.cmd, Color = item.color },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{x - actHalfW} {y - actHalfH}",
                            OffsetMax = $"{x + actHalfW} {y + actHalfH}",
                        },
                        Text =
                        {
                            Text = Msg(item.langKey, player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = uiCfg.Actions.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = uiCfg.Actions.TextColor,
                        },
                    },
                    LayerContentBody,
                    $"UI_Act_{item.key}"
                );
            }
        }

        #endregion

        #region Content Page: Permission Manager

        // CHANGE: Страница менеджера прав и групп (двухуровневая навигация, пагинация, поиск, управление группами и игроками)
        private void RenderPermissionManagerContent(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var permCfg = _config.PermissionManager;

            bool isGroupMode = (session.PermTargetType == "group");

            int tabW = permCfg.Tabs.Width;
            int cgW = permCfg.CreateGroupButton.Width;
            int tabH = permCfg.Tabs.Height;
            int tabHalfH = tabH / 2;
            int cgHalfH = permCfg.CreateGroupButton.Height / 2;
            int gap = permCfg.GroupsBar.Spacing;

            if (isGroupMode)
            {
                int totalW = tabW + gap + tabW + gap + cgW;
                int startX = -totalW / 2;

                int b1MinX = startX;
                int b1MaxX = b1MinX + tabW;

                int b2MinX = b1MaxX + gap;
                int b2MaxX = b2MinX + tabW;

                int b3MinX = b2MaxX + gap;
                int b3MaxX = b3MinX + cgW;

                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = "radminmenu.perm_mode group",
                            Color = permCfg.Tabs.ActiveColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{b1MinX} {permCfg.Tabs.OffsetY - tabHalfH}",
                            OffsetMax = $"{b1MaxX} {permCfg.Tabs.OffsetY + tabHalfH}",
                        },
                        Text =
                        {
                            Text = Msg("PERM_GROUPS", player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = permCfg.Tabs.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.Tabs.TextColor,
                        },
                    },
                    LayerContentBody,
                    "Perm_Tab_Group"
                );

                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = "radminmenu.perm_mode user",
                            Color = permCfg.Tabs.InactiveColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{b2MinX} {permCfg.Tabs.OffsetY - tabHalfH}",
                            OffsetMax = $"{b2MaxX} {permCfg.Tabs.OffsetY + tabHalfH}",
                        },
                        Text =
                        {
                            Text = Msg("PERM_USERS", player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = permCfg.Tabs.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.Tabs.TextColor,
                        },
                    },
                    LayerContentBody,
                    "Perm_Tab_User"
                );

                // CHANGE: Позиционирование кнопки создания группы с учетом CreateGroupBtnHeight
                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = "radminmenu.modal_open creategroup",
                            Color = permCfg.CreateGroupButton.BackgroundColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{b3MinX} {permCfg.Tabs.OffsetY - cgHalfH}",
                            OffsetMax = $"{b3MaxX} {permCfg.Tabs.OffsetY + cgHalfH}",
                        },
                        Text =
                        {
                            Text = Msg("PERM_CREATE_GROUP", player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = permCfg.CreateGroupButton.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.CreateGroupButton.TextColor,
                        },
                    },
                    LayerContentBody,
                    "Perm_CreateGroupBtn"
                );

                string[] groups = permission.GetGroups();
                if (groups == null || groups.Length == 0)
                    groups = new[] { "default" };

                if (
                    string.IsNullOrEmpty(session.PermTargetName)
                    || !groups.Contains(session.PermTargetName)
                )
                    session.PermTargetName = groups[0];

                int groupBtnHalfW = permCfg.GroupsBar.Width / 2;
                int groupBtnHalfH = permCfg.GroupsBar.Height / 2;

                int totalGroupsWidth =
                    groups.Length * permCfg.GroupsBar.Width
                    + (groups.Length - 1) * permCfg.GroupsBar.Spacing;
                float contentWidth = Mathf.Max(
                    (float)permCfg.GroupsBar.ScrollWidth,
                    totalGroupsWidth + 10f
                );
                float startGroupX =
                    (totalGroupsWidth < permCfg.GroupsBar.ScrollWidth)
                        ? (permCfg.GroupsBar.ScrollWidth - totalGroupsWidth) / 2f
                        : 5f;

                int gScrollHalfW = permCfg.GroupsBar.ScrollWidth / 2;

                // CHANGE: Горизонтальный ScrollView для строки групп
                container.Add(
                    new CuiElement
                    {
                        Parent = LayerContentBody,
                        Name = "Perm_GroupsScroll",
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{-gScrollHalfW} {permCfg.GroupsBar.OffsetY - groupBtnHalfH - 2}",
                                OffsetMax =
                                    $"{gScrollHalfW} {permCfg.GroupsBar.OffsetY + groupBtnHalfH + 2}",
                            },
                            new CuiImageComponent
                            {
                                Color = "0 0 0 0",
                            },
                            new CuiRectMask2DComponent(),
                            new CuiScrollViewComponent
                            {
                                Vertical = false,
                                Horizontal = true,
                                Inertia = true,
                                MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                                ScrollSensitivity = SCROLL_SENSITIVITY,
                                ContentTransform = new CuiRectTransformComponent
                                {
                                    AnchorMin = "0 0",
                                    AnchorMax = "0 1",
                                    OffsetMin = "0 0",
                                    OffsetMax = $"{contentWidth} 0",
                                },
                            },
                        },
                    }
                );

                for (int i = 0; i < groups.Length; i++)
                {
                    string groupName = groups[i];
                    bool isSelected = string.Equals(
                        session.PermTargetName,
                        groupName,
                        StringComparison.OrdinalIgnoreCase
                    );
                    float gx =
                        startGroupX + i * (permCfg.GroupsBar.Width + permCfg.GroupsBar.Spacing);

                    string btnColor = isSelected
                        ? permCfg.GroupsBar.ActiveGroupColor
                        : permCfg.GroupsBar.InactiveGroupColor;
                    // CHANGE: Отображение титула группы (Title) с сохранением регистра букв (VIP, ADMIN и т.д.)
                    string groupTitle = permission.GetGroupTitle(groupName);
                    string displayName = !string.IsNullOrWhiteSpace(groupTitle)
                        ? groupTitle
                        : groupName;

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = $"radminmenu.perm_select_target {groupName}",
                                Color = btnColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0.5",
                                AnchorMax = "0 0.5",
                                OffsetMin = $"{gx} {-groupBtnHalfH}",
                                OffsetMax = $"{gx + permCfg.GroupsBar.Width} {groupBtnHalfH}",
                            },
                            Text =
                            {
                                Text = displayName,
                                Align = TextAnchor.MiddleCenter,
                                FontSize = permCfg.GroupsBar.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.GroupsBar.TextColor,
                            },
                        },
                        "Perm_GroupsScroll",
                        $"PermGroup_{groupName}"
                    );
                }

                var pluginsPerms = GetRegisteredPermissionsByPlugin();

                if (string.IsNullOrEmpty(session.PermSelectedPlugin))
                {
                    int sHalfW = permCfg.Search.Width / 2;
                    int sHalfH = permCfg.Search.Height / 2;

                    container.Add(
                        new CuiPanel
                        {
                            Image = { Color = permCfg.Search.BackgroundColor },
                            RectTransform =
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{permCfg.Search.OffsetX - sHalfW} {permCfg.Search.OffsetY - sHalfH}",
                                OffsetMax =
                                    $"{permCfg.Search.OffsetX + sHalfW} {permCfg.Search.OffsetY + sHalfH}",
                            },
                        },
                        LayerContentBody,
                        "PermSearchBg"
                    );

                    // CHANGE: Интерактивный плейсхолдер поиска прав групп: отображается когда пусто, исчезает по клику
                    bool showPermPlaceholder =
                        string.IsNullOrEmpty(session.PermSearch)
                        && session.FocusedInput != "perm_search";

                    if (showPermPlaceholder)
                    {
                        container.Add(
                            new CuiButton
                            {
                                Button =
                                {
                                    Command = "radminmenu.focus perm_search",
                                    Color = "0 0 0 0",
                                },
                                RectTransform =
                                {
                                    AnchorMin = "0 0",
                                    AnchorMax = "1 1",
                                    OffsetMin = $"{permCfg.Search.PaddingX} 0",
                                    OffsetMax = $"{-permCfg.Search.PaddingX} 0",
                                },
                                Text =
                                {
                                    Text = Msg("PERM_SEARCH_PLACEHOLDER", player.UserIDString),
                                    FontSize = permCfg.Search.FontSize,
                                    Font = "robotocondensed-regular.ttf",
                                    Align = TextAnchor.MiddleLeft,
                                    Color = permCfg.Search.PlaceholderColor,
                                },
                            },
                            "PermSearchBg",
                            "PermSearchPlaceholderBtn"
                        );
                    }
                    else
                    {
                        container.Add(
                            new CuiElement
                            {
                                Parent = "PermSearchBg",
                                Name = "PermSearchInput",
                                Components =
                                {
                                    new CuiInputFieldComponent
                                    {
                                        Text = session.PermSearch,
                                        Command = "radminmenu.perm_search ",
                                        Align = TextAnchor.MiddleLeft,
                                        FontSize = permCfg.Search.FontSize,
                                        Font = "robotocondensed-regular.ttf",
                                        Color = permCfg.Search.TextColor,
                                        CharsLimit = 32,
                                        NeedsKeyboard = true,
                                        Autofocus = (session.FocusedInput == "perm_search"),
                                    },
                                    new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0 0",
                                        AnchorMax = "1 1",
                                        OffsetMin = $"{permCfg.Search.PaddingX} 0",
                                        OffsetMax = $"{-permCfg.Search.PaddingX} 0",
                                    },
                                },
                            }
                        );
                    }

                    int actHalfW = permCfg.GroupActions.Width / 2;
                    int actHalfH = permCfg.GroupActions.Height / 2;

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = "radminmenu.modal_open clonegroup",
                                Color = permCfg.GroupActions.CloneButtonColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{permCfg.GroupActions.CloneOffsetX - actHalfW} {permCfg.GroupActions.OffsetY - actHalfH}",
                                OffsetMax =
                                    $"{permCfg.GroupActions.CloneOffsetX + actHalfW} {permCfg.GroupActions.OffsetY + actHalfH}",
                            },
                            Text =
                            {
                                Text = Msg("PERM_CLONE_GROUP", player.UserIDString),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = permCfg.GroupActions.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.GroupActions.TextColor,
                            },
                        },
                        LayerContentBody,
                        "Perm_CloneGroupBtn"
                    );

                    if (session.PermTargetName != "default" && session.PermTargetName != "admin")
                    {
                        // CHANGE: Открытие модального окна подтверждения удаления группы
                        container.Add(
                            new CuiButton
                            {
                                Button =
                                {
                                    Command = "radminmenu.modal_open deletegroup",
                                    Color = permCfg.GroupActions.DeleteButtonColor,
                                },
                                RectTransform =
                                {
                                    AnchorMin = "0.5 0.5",
                                    AnchorMax = "0.5 0.5",
                                    OffsetMin =
                                        $"{permCfg.GroupActions.DeleteOffsetX - actHalfW} {permCfg.GroupActions.OffsetY - actHalfH}",
                                    OffsetMax =
                                        $"{permCfg.GroupActions.DeleteOffsetX + actHalfW} {permCfg.GroupActions.OffsetY + actHalfH}",
                                },
                                Text =
                                {
                                    Text = Msg("PERM_REMOVE_GROUP", player.UserIDString),
                                    Align = TextAnchor.MiddleCenter,
                                    FontSize = permCfg.GroupActions.FontSize,
                                    Font = "robotocondensed-bold.ttf",
                                    Color = permCfg.GroupActions.TextColor,
                                },
                            },
                            LayerContentBody,
                            "Perm_RemoveGroupBtn"
                        );
                    }

                    var pluginList = pluginsPerms
                        .Keys.Where(p =>
                            string.IsNullOrEmpty(session.PermSearch)
                            || p.IndexOf(session.PermSearch, StringComparison.OrdinalIgnoreCase)
                                >= 0
                        )
                        .OrderBy(p => p)
                        .ToList();

                    int cols = permCfg.PluginGrid.Columns;
                    int rows = Mathf.CeilToInt((float)pluginList.Count / cols);
                    if (rows < 1)
                        rows = 1;

                    const int autoScrollHalfW = 330;
                    const int autoScrollHalfH = 150;
                    float viewHeight = 300f;

                    float totalHeight = Mathf.Max(
                        viewHeight,
                        10f
                            + rows * permCfg.PluginGrid.CardHeight
                            + (rows - 1) * permCfg.PluginGrid.SpacingY
                            + 10f
                    );

                    container.Add(
                        new CuiElement
                        {
                            Parent = LayerContentBody,
                            Name = "Perm_PluginScroll",
                            Components =
                            {
                                new CuiRectTransformComponent
                                {
                                    AnchorMin = "0.5 0.5",
                                    AnchorMax = "0.5 0.5",
                                    OffsetMin = $"{-autoScrollHalfW} {-65 - autoScrollHalfH}",
                                    OffsetMax = $"{autoScrollHalfW} {-65 + autoScrollHalfH}",
                                },
                                new CuiImageComponent
                                {
                                    Color = "0 0 0 0",
                                },
                                new CuiRectMask2DComponent(),
                                new CuiScrollViewComponent
                                {
                                    Vertical = true,
                                    Horizontal = false,
                                    Inertia = true,
                                    MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                                    ScrollSensitivity = SCROLL_SENSITIVITY,
                                    ContentTransform = new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0 1",
                                        AnchorMax = "1 1",
                                        OffsetMin = $"0 {-totalHeight}",
                                        OffsetMax = "0 0",
                                    },
                                },
                            },
                        }
                    );

                    for (int i = 0; i < pluginList.Count; i++)
                    {
                        string pName = pluginList[i];
                        var pPerms = pluginsPerms[pName];
                        int totalPerms = pPerms.Count;
                        int grantedPerms = pPerms.Count(perm =>
                            permission.GroupHasPermission(session.PermTargetName, perm)
                        );

                        int col = i % cols;
                        int row = i / cols;

                        float px =
                            5f + col * (permCfg.PluginGrid.CardWidth + permCfg.PluginGrid.SpacingX);
                        float py =
                            5f
                            + row * (permCfg.PluginGrid.CardHeight + permCfg.PluginGrid.SpacingY);

                        string cardBgColor =
                            (grantedPerms > 0)
                                ? permCfg.PluginGrid.CardBackgroundColor
                                : permCfg.PermGrid.RevokedColor;
                        string badgeColor =
                            (grantedPerms > 0)
                                ? permCfg.PluginGrid.CountColor
                                : permCfg.UserMode.DetailsColor;
                        string cardElemName = $"PermPlugin_{pName}";

                        container.Add(
                            new CuiButton
                            {
                                Button =
                                {
                                    Command = $"radminmenu.perm_select_plugin {pName}",
                                    Color = cardBgColor,
                                },
                                RectTransform =
                                {
                                    AnchorMin = "0 1",
                                    AnchorMax = "0 1",
                                    OffsetMin = $"{px} {-py - permCfg.PluginGrid.CardHeight}",
                                    OffsetMax = $"{px + permCfg.PluginGrid.CardWidth} {-py}",
                                },
                                Text =
                                {
                                    Text = $"  <b>{pName}</b>",
                                    Align = TextAnchor.MiddleLeft,
                                    FontSize = permCfg.PluginGrid.FontSize,
                                    Font = "robotocondensed-bold.ttf",
                                    Color = permCfg.PluginGrid.TextColor,
                                },
                            },
                            "Perm_PluginScroll",
                            cardElemName
                        );

                        container.Add(
                            new CuiLabel
                            {
                                Text =
                                {
                                    Text = $"{grantedPerms}/{totalPerms}  ",
                                    Align = TextAnchor.MiddleRight,
                                    FontSize = permCfg.PluginGrid.FontSize,
                                    Font = "robotocondensed-bold.ttf",
                                    Color = badgeColor,
                                },
                                RectTransform =
                                {
                                    AnchorMin = "0 0",
                                    AnchorMax = "1 1",
                                    OffsetMin = "0 0",
                                    OffsetMax = "0 0",
                                },
                            },
                            cardElemName
                        );
                    }
                }
                else
                {
                    int bHalfW = permCfg.BulkActions.BackWidth / 2;
                    int bHalfH = permCfg.BulkActions.BackHeight / 2;

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = "radminmenu.perm_select_plugin back",
                                Color = permCfg.BulkActions.BackButtonColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{permCfg.BulkActions.BackOffsetX - bHalfW} {permCfg.BulkActions.OffsetY - bHalfH}",
                                OffsetMax =
                                    $"{permCfg.BulkActions.BackOffsetX + bHalfW} {permCfg.BulkActions.OffsetY + bHalfH}",
                            },
                            Text =
                            {
                                Text = Msg("PERM_BACK_TO_PLUGINS", player.UserIDString),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = permCfg.BulkActions.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.BulkActions.TextColor,
                            },
                        },
                        LayerContentBody,
                        "Perm_BackToPluginsBtn"
                    );

                    // CHANGE: Отображение титула группы с правильным регистром букв в заголовке прав плагина
                    string pGroupTitle = permission.GetGroupTitle(session.PermTargetName);
                    string pGroupDisplay = !string.IsNullOrWhiteSpace(pGroupTitle)
                        ? pGroupTitle
                        : session.PermTargetName;

                    int pluginTitleHalfW = permCfg.BulkActions.PluginTitleWidth / 2;
                    container.Add(
                        new CuiLabel
                        {
                            Text =
                            {
                                Text =
                                    $"<b>{session.PermSelectedPlugin}</b>  (<color={permCfg.UserMode.HeaderColor}>{pGroupDisplay}</color>)",
                                Align = TextAnchor.MiddleCenter,
                                FontSize = permCfg.UserMode.HeaderFontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.Tabs.TextColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{permCfg.BulkActions.PluginTitleOffsetX - pluginTitleHalfW} {permCfg.BulkActions.OffsetY - bHalfH}",
                                OffsetMax =
                                    $"{permCfg.BulkActions.PluginTitleOffsetX + pluginTitleHalfW} {permCfg.BulkActions.OffsetY + bHalfH}",
                            },
                        },
                        LayerContentBody
                    );

                    int bulkHalfW = permCfg.BulkActions.ActionWidth / 2;
                    int bulkHalfH = permCfg.BulkActions.ActionHeight / 2;

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command =
                                    $"radminmenu.perm_grant_all_group {session.PermTargetName} {session.PermSelectedPlugin}",
                                Color = permCfg.BulkActions.GrantAllColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{permCfg.BulkActions.GrantAllOffsetX - bulkHalfW} {permCfg.BulkActions.OffsetY - bulkHalfH}",
                                OffsetMax =
                                    $"{permCfg.BulkActions.GrantAllOffsetX + bulkHalfW} {permCfg.BulkActions.OffsetY + bulkHalfH}",
                            },
                            Text =
                            {
                                Text = Msg("PERM_GRANT_ALL", player.UserIDString),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = permCfg.BulkActions.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.BulkActions.TextColor,
                            },
                        },
                        LayerContentBody,
                        "Perm_GrantAllGroupBtn"
                    );

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command =
                                    $"radminmenu.perm_revoke_all_group {session.PermTargetName} {session.PermSelectedPlugin}",
                                Color = permCfg.BulkActions.RevokeAllColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin =
                                    $"{permCfg.BulkActions.RevokeAllOffsetX - bulkHalfW} {permCfg.BulkActions.OffsetY - bulkHalfH}",
                                OffsetMax =
                                    $"{permCfg.BulkActions.RevokeAllOffsetX + bulkHalfW} {permCfg.BulkActions.OffsetY + bulkHalfH}",
                            },
                            Text =
                            {
                                Text = Msg("PERM_REVOKE_ALL", player.UserIDString),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = permCfg.BulkActions.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.BulkActions.TextColor,
                            },
                        },
                        LayerContentBody,
                        "Perm_RevokeAllGroupBtn"
                    );

                    List<string> permsList;
                    if (!pluginsPerms.TryGetValue(session.PermSelectedPlugin, out permsList))
                        permsList = new List<string>();

                    int cols = permCfg.PermGrid.Columns;
                    int rows = Mathf.CeilToInt((float)permsList.Count / cols);
                    if (rows < 1)
                        rows = 1;

                    const int permAutoScrollHalfW = 330;
                    const int permAutoScrollHalfH = 150;
                    float viewHeight = 300f;

                    float totalHeight = Mathf.Max(
                        viewHeight,
                        10f
                            + rows * permCfg.PermGrid.CardHeight
                            + (rows - 1) * permCfg.PermGrid.SpacingY
                            + 10f
                    );

                    container.Add(
                        new CuiElement
                        {
                            Parent = LayerContentBody,
                            Name = "Perm_PermScroll",
                            Components =
                            {
                                new CuiRectTransformComponent
                                {
                                    AnchorMin = "0.5 0.5",
                                    AnchorMax = "0.5 0.5",
                                    OffsetMin = $"{-permAutoScrollHalfW} {-65 - permAutoScrollHalfH}",
                                    OffsetMax = $"{permAutoScrollHalfW} {-65 + permAutoScrollHalfH}",
                                },
                                new CuiImageComponent
                                {
                                    Color = "0 0 0 0",
                                },
                                new CuiRectMask2DComponent(),
                                new CuiScrollViewComponent
                                {
                                    Vertical = true,
                                    Horizontal = false,
                                    Inertia = true,
                                    MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                                    ScrollSensitivity = SCROLL_SENSITIVITY,
                                    ContentTransform = new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0 1",
                                        AnchorMax = "1 1",
                                        OffsetMin = $"0 {-totalHeight}",
                                        OffsetMax = "0 0",
                                    },
                                },
                            },
                        }
                    );

                    for (int i = 0; i < permsList.Count; i++)
                    {
                        string perm = permsList[i];
                        bool hasPerm = permission.GroupHasPermission(session.PermTargetName, perm);

                        int col = i % cols;
                        int row = i / cols;

                        float px =
                            5f + col * (permCfg.PermGrid.CardWidth + permCfg.PermGrid.SpacingX);
                        float py =
                            5f + row * (permCfg.PermGrid.CardHeight + permCfg.PermGrid.SpacingY);

                        string btnColor = hasPerm
                            ? permCfg.PermGrid.GrantedColor
                            : permCfg.PermGrid.RevokedColor;
                        string statusIcon = hasPerm ? "✔" : "✖";
                        string permElemName = $"GroupPermToggle_{i}";

                        container.Add(
                            new CuiButton
                            {
                                Button =
                                {
                                    Command =
                                        $"radminmenu.perm_toggle_group_perm {session.PermTargetName} {perm}",
                                    Color = btnColor,
                                },
                                RectTransform =
                                {
                                    AnchorMin = "0 1",
                                    AnchorMax = "0 1",
                                    OffsetMin = $"{px} {-py - permCfg.PermGrid.CardHeight}",
                                    OffsetMax = $"{px + permCfg.PermGrid.CardWidth} {-py}",
                                },
                                Text =
                                {
                                    Text = $"  {perm}",
                                    Align = TextAnchor.MiddleLeft,
                                    FontSize = permCfg.PermGrid.FontSize,
                                    Font = "robotocondensed-bold.ttf",
                                    Color = permCfg.PermGrid.TextColor,
                                },
                            },
                            "Perm_PermScroll",
                            permElemName
                        );

                        container.Add(
                            new CuiLabel
                            {
                                Text =
                                {
                                    Text = $"{statusIcon}  ",
                                    Align = TextAnchor.MiddleRight,
                                    FontSize = permCfg.PermGrid.FontSize + 2,
                                    Font = "robotocondensed-bold.ttf",
                                    Color = permCfg.PermGrid.TextColor,
                                },
                                RectTransform =
                                {
                                    AnchorMin = "0 0",
                                    AnchorMax = "1 1",
                                    OffsetMin = "0 0",
                                    OffsetMax = "0 0",
                                },
                            },
                            permElemName
                        );
                    }
                }
            }
            else
            {
                RenderUserPermissionsView(container, player, session, permCfg);
            }
        }

        // CHANGE: Отображение прав игрока либо списка игроков для выбора
        private void RenderUserPermissionsView(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session,
            PermissionManagerSettings permCfg
        )
        {
            int tabW = permCfg.Tabs.Width;
            int tabH = permCfg.Tabs.Height;
            int tabHalfH = tabH / 2;
            int gap = permCfg.GroupsBar.Spacing;

            int totalW = tabW + gap + tabW;
            int startX = -totalW / 2;

            int b1MinX = startX;
            int b1MaxX = b1MinX + tabW;

            int b2MinX = b1MaxX + gap;
            int b2MaxX = b2MinX + tabW;

            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.perm_mode group",
                        Color = permCfg.Tabs.InactiveColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = $"{b1MinX} {permCfg.Tabs.OffsetY - tabHalfH}",
                        OffsetMax = $"{b1MaxX} {permCfg.Tabs.OffsetY + tabHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("PERM_GROUPS", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = permCfg.Tabs.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = permCfg.Tabs.TextColor,
                    },
                },
                LayerContentBody,
                "Perm_Tab_Group"
            );

            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.perm_mode user",
                        Color = permCfg.Tabs.ActiveColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = $"{b2MinX} {permCfg.Tabs.OffsetY - tabHalfH}",
                        OffsetMax = $"{b2MaxX} {permCfg.Tabs.OffsetY + tabHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("PERM_USERS", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = permCfg.Tabs.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = permCfg.Tabs.TextColor,
                    },
                },
                LayerContentBody,
                "Perm_Tab_User"
            );

            if (session.SelectedUserId == 0)
            {
                RenderUserSelectionList(container, player, session, permCfg);
                return;
            }

            ulong targetUserId = session.SelectedUserId;
            IPlayer targetUser = covalence.Players.FindPlayerById(targetUserId.ToString());
            string targetName = targetUser?.Name ?? targetUserId.ToString();

            int bHalfW = permCfg.BulkActions.BackWidth / 2;
            int bHalfH = permCfg.BulkActions.BackHeight / 2;

            // 1. Верхняя строка игрока: Кнопка возврата к списку игроков и имя
            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.perm_select_user 0",
                        Color = permCfg.BulkActions.BackButtonColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{permCfg.BulkActions.BackOffsetX - bHalfW} {permCfg.UserMode.UserTitleOffsetY - bHalfH}",
                        OffsetMax =
                            $"{permCfg.BulkActions.BackOffsetX + bHalfW} {permCfg.UserMode.UserTitleOffsetY + bHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("PERM_BACK_TO_USERS", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = permCfg.BulkActions.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = permCfg.BulkActions.TextColor,
                    },
                },
                LayerContentBody,
                "Perm_BackToUsersBtn"
            );

            int uTitleHalfW = permCfg.UserMode.UserTitleWidth / 2;
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = Msg(
                            "PERM_USER_GROUPS_TITLE",
                            player.UserIDString,
                            $"<b>{targetName}</b> ({targetUserId})"
                        ),
                        Align = TextAnchor.MiddleLeft,
                        FontSize = permCfg.UserMode.HeaderFontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = permCfg.UserMode.HeaderColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{permCfg.UserMode.UserTitleOffsetX - uTitleHalfW} {permCfg.UserMode.UserTitleOffsetY - bHalfH}",
                        OffsetMax =
                            $"{permCfg.UserMode.UserTitleOffsetX + uTitleHalfW} {permCfg.UserMode.UserTitleOffsetY + bHalfH}",
                    },
                },
                LayerContentBody
            );

            // 2. Строка групп игрока через горизонтальный ScrollView
            string[] allGroups = permission.GetGroups();
            if (allGroups == null || allGroups.Length == 0)
                allGroups = new[] { "default" };

            int gHalfW = permCfg.GroupsBar.Width / 2;
            int gHalfH = permCfg.GroupsBar.Height / 2;
            int totalGroupsW =
                allGroups.Length * permCfg.GroupsBar.Width
                + (allGroups.Length - 1) * permCfg.GroupsBar.Spacing;
            float contentW = Mathf.Max((float)permCfg.GroupsBar.ScrollWidth, totalGroupsW + 10f);
            float startGX =
                (totalGroupsW < permCfg.GroupsBar.ScrollWidth)
                    ? (permCfg.GroupsBar.ScrollWidth - totalGroupsW) / 2f
                    : 5f;

            int gScrollHalfWUser = permCfg.GroupsBar.ScrollWidth / 2;

            container.Add(
                new CuiElement
                {
                    Parent = LayerContentBody,
                    Name = "User_GroupsScroll",
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{-gScrollHalfWUser} {permCfg.UserMode.UserGroupsOffsetY - gHalfH - 2}",
                            OffsetMax =
                                $"{gScrollHalfWUser} {permCfg.UserMode.UserGroupsOffsetY + gHalfH + 2}",
                        },
                        new CuiImageComponent
                        {
                            Color = "0 0 0 0",
                        },
                        new CuiRectMask2DComponent(),
                        new CuiScrollViewComponent
                        {
                            Vertical = false,
                            Horizontal = true,
                            Inertia = true,
                            MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                            ScrollSensitivity = SCROLL_SENSITIVITY,
                            ContentTransform = new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 1",
                                OffsetMin = "0 0",
                                OffsetMax = $"{contentW} 0",
                            },
                        },
                    },
                }
            );

            for (int i = 0; i < allGroups.Length; i++)
            {
                string groupName = allGroups[i];
                bool inGroup = permission.UserHasGroup(targetUserId.ToString(), groupName);
                float gx = startGX + i * (permCfg.GroupsBar.Width + permCfg.GroupsBar.Spacing);

                string btnColor = inGroup
                    ? permCfg.PermGrid.GrantedColor
                    : permCfg.PermGrid.RevokedColor;

                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command =
                                $"radminmenu.perm_toggle_user_group {targetUserId} {groupName}",
                            Color = btnColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin = $"{gx} {-gHalfH}",
                            OffsetMax = $"{gx + permCfg.GroupsBar.Width} {gHalfH}",
                        },
                        Text =
                        {
                            Text = inGroup ? $"✔ {groupName}" : groupName,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = permCfg.GroupsBar.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.GroupsBar.TextColor,
                        },
                    },
                    "User_GroupsScroll",
                    $"UserGroup_{groupName}"
                );
            }

            var pluginsPerms = GetRegisteredPermissionsByPlugin();

            // 3. Плагины и права игрока
            if (string.IsNullOrEmpty(session.PermSelectedPlugin))
            {
                int sHalfW = permCfg.Search.Width / 2;
                int sHalfH = permCfg.Search.Height / 2;

                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = permCfg.Search.BackgroundColor },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{permCfg.Search.OffsetX - sHalfW} {permCfg.UserMode.UserActionRowOffsetY - sHalfH}",
                            OffsetMax =
                                $"{permCfg.Search.OffsetX + sHalfW} {permCfg.UserMode.UserActionRowOffsetY + sHalfH}",
                        },
                    },
                    LayerContentBody,
                    "UserPermSearchBg"
                );

                // CHANGE: Интерактивный плейсхолдер поиска прав игрока: отображается когда пусто, исчезает по клику
                bool showUserPermPlaceholder =
                    string.IsNullOrEmpty(session.PermSearch)
                    && session.FocusedInput != "user_perm_search";

                if (showUserPermPlaceholder)
                {
                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = "radminmenu.focus user_perm_search",
                                Color = "0 0 0 0",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = $"{permCfg.Search.PaddingX} 0",
                                OffsetMax = $"{-permCfg.Search.PaddingX} 0",
                            },
                            Text =
                            {
                                Text = Msg("PERM_SEARCH_PLACEHOLDER", player.UserIDString),
                                FontSize = permCfg.Search.FontSize,
                                Font = "robotocondensed-regular.ttf",
                                Align = TextAnchor.MiddleLeft,
                                Color = permCfg.Search.PlaceholderColor,
                            },
                        },
                        "UserPermSearchBg",
                        "UserPermSearchPlaceholderBtn"
                    );
                }
                else
                {
                    container.Add(
                        new CuiElement
                        {
                            Parent = "UserPermSearchBg",
                            Name = "UserPermSearchInput",
                            Components =
                            {
                                new CuiInputFieldComponent
                                {
                                    Text = session.PermSearch,
                                    Command = "radminmenu.perm_search ",
                                    Align = TextAnchor.MiddleLeft,
                                    FontSize = permCfg.Search.FontSize,
                                    Font = "robotocondensed-regular.ttf",
                                    Color = permCfg.Search.TextColor,
                                    CharsLimit = 32,
                                    NeedsKeyboard = true,
                                    Autofocus = (session.FocusedInput == "user_perm_search"),
                                },
                                new CuiRectTransformComponent
                                {
                                    AnchorMin = "0 0",
                                    AnchorMax = "1 1",
                                    OffsetMin = $"{permCfg.Search.PaddingX} 0",
                                    OffsetMax = $"{-permCfg.Search.PaddingX} 0",
                                },
                            },
                        }
                    );
                }

                var userData = permission.GetUserData(targetUserId.ToString());
                var directPermsSet =
                    userData != null && userData.Perms != null
                        ? userData.Perms
                        : new HashSet<string>();

                int uPermTitleHalfW = permCfg.UserMode.UserPermTitleWidth / 2;
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Msg(
                                "PERM_USER_PERMS_TITLE",
                                player.UserIDString,
                                directPermsSet.Count
                            ),
                            Align = TextAnchor.MiddleLeft,
                            FontSize = permCfg.UserMode.ItemFontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.UserMode.DetailsColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{permCfg.UserMode.UserPermTitleOffsetX - uPermTitleHalfW} {permCfg.UserMode.UserActionRowOffsetY - sHalfH}",
                            OffsetMax =
                                $"{permCfg.UserMode.UserPermTitleOffsetX + uPermTitleHalfW} {permCfg.UserMode.UserActionRowOffsetY + sHalfH}",
                        },
                    },
                    LayerContentBody
                );

                var pluginList = pluginsPerms
                    .Keys.Where(p =>
                        string.IsNullOrEmpty(session.PermSearch)
                        || p.IndexOf(session.PermSearch, StringComparison.OrdinalIgnoreCase) >= 0
                    )
                    .OrderBy(p => p)
                    .ToList();

                int cols = permCfg.PluginGrid.Columns;
                int rows = Mathf.CeilToInt((float)pluginList.Count / cols);
                if (rows < 1)
                    rows = 1;

                const int uAutoScrollHalfW = 330;
                const int uAutoScrollHalfH = 134;
                float viewHeight = 268f;

                float totalHeight = Mathf.Max(
                    viewHeight,
                    10f
                        + rows * permCfg.PluginGrid.CardHeight
                        + (rows - 1) * permCfg.PluginGrid.SpacingY
                        + 10f
                );

                container.Add(
                    new CuiElement
                    {
                        Parent = LayerContentBody,
                        Name = "UserPerm_PluginScroll",
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin = $"{-uAutoScrollHalfW} {-84 - uAutoScrollHalfH}",
                                OffsetMax = $"{uAutoScrollHalfW} {-84 + uAutoScrollHalfH}",
                            },
                            new CuiImageComponent
                            {
                                Color = "0 0 0 0",
                            },
                            new CuiRectMask2DComponent(),
                            new CuiScrollViewComponent
                            {
                                Vertical = true,
                                Horizontal = false,
                                Inertia = true,
                                MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                                ScrollSensitivity = SCROLL_SENSITIVITY,
                                ContentTransform = new CuiRectTransformComponent
                                {
                                    AnchorMin = "0 1",
                                    AnchorMax = "1 1",
                                    OffsetMin = $"0 {-totalHeight}",
                                    OffsetMax = "0 0",
                                },
                            },
                        },
                    }
                );

                for (int i = 0; i < pluginList.Count; i++)
                {
                    string pName = pluginList[i];
                    var pPerms = pluginsPerms[pName];
                    int totalPerms = pPerms.Count;

                    int directCount = pPerms.Count(perm => directPermsSet.Contains(perm));
                    int groupCount = pPerms.Count(perm =>
                        !directPermsSet.Contains(perm)
                        && permission.UserHasPermission(targetUserId.ToString(), perm)
                    );
                    int totalGranted = directCount + groupCount;

                    int col = i % cols;
                    int row = i / cols;

                    float px =
                        5f + col * (permCfg.PluginGrid.CardWidth + permCfg.PluginGrid.SpacingX);
                    float py =
                        5f + row * (permCfg.PluginGrid.CardHeight + permCfg.PluginGrid.SpacingY);

                    string cardBgColor =
                        (totalGranted > 0)
                            ? permCfg.PluginGrid.CardBackgroundColor
                            : permCfg.PermGrid.RevokedColor;
                    string badgeColor =
                        (directCount > 0)
                            ? permCfg.PermGrid.GrantedColor
                            : (
                                (groupCount > 0)
                                    ? permCfg.UserMode.HeaderColor
                                    : permCfg.UserMode.DetailsColor
                            );
                    string cardElemName = $"UserPermPlugin_{pName}";

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = $"radminmenu.perm_select_plugin {pName}",
                                Color = cardBgColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 1",
                                AnchorMax = "0 1",
                                OffsetMin = $"{px} {-py - permCfg.PluginGrid.CardHeight}",
                                OffsetMax = $"{px + permCfg.PluginGrid.CardWidth} {-py}",
                            },
                            Text =
                            {
                                Text = $"  <b>{pName}</b>",
                                Align = TextAnchor.MiddleLeft,
                                FontSize = permCfg.PluginGrid.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.PluginGrid.TextColor,
                            },
                        },
                        "UserPerm_PluginScroll",
                        cardElemName
                    );

                    container.Add(
                        new CuiLabel
                        {
                            Text =
                            {
                                Text = $"{totalGranted}/{totalPerms}  ",
                                Align = TextAnchor.MiddleRight,
                                FontSize = permCfg.PluginGrid.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = badgeColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = "0 0",
                                OffsetMax = "0 0",
                            },
                        },
                        cardElemName
                    );
                }
            }
            else
            {
                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = "radminmenu.perm_select_plugin back",
                            Color = permCfg.BulkActions.BackButtonColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{permCfg.BulkActions.BackOffsetX - bHalfW} {permCfg.UserMode.UserActionRowOffsetY - bHalfH}",
                            OffsetMax =
                                $"{permCfg.BulkActions.BackOffsetX + bHalfW} {permCfg.UserMode.UserActionRowOffsetY + bHalfH}",
                        },
                        Text =
                        {
                            Text = Msg("PERM_BACK_TO_PLUGINS", player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = permCfg.BulkActions.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.BulkActions.TextColor,
                        },
                    },
                    LayerContentBody,
                    "Perm_BackToPluginsUserBtn"
                );

                int pluginTitleHalfW = permCfg.BulkActions.PluginTitleWidth / 2;
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text =
                                $"<b>{session.PermSelectedPlugin}</b>  (<color={permCfg.UserMode.HeaderColor}>{targetName}</color>)",
                            Align = TextAnchor.MiddleCenter,
                            FontSize = permCfg.UserMode.HeaderFontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.Tabs.TextColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{permCfg.BulkActions.PluginTitleOffsetX - pluginTitleHalfW} {permCfg.UserMode.UserActionRowOffsetY - bHalfH}",
                            OffsetMax =
                                $"{permCfg.BulkActions.PluginTitleOffsetX + pluginTitleHalfW} {permCfg.UserMode.UserActionRowOffsetY + bHalfH}",
                        },
                    },
                    LayerContentBody
                );

                int bulkHalfW = permCfg.BulkActions.ActionWidth / 2;
                int bulkHalfH = permCfg.BulkActions.ActionHeight / 2;

                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command =
                                $"radminmenu.perm_grant_all_user {targetUserId} {session.PermSelectedPlugin}",
                            Color = permCfg.BulkActions.GrantAllColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{permCfg.BulkActions.GrantAllOffsetX - bulkHalfW} {permCfg.UserMode.UserActionRowOffsetY - bulkHalfH}",
                            OffsetMax =
                                $"{permCfg.BulkActions.GrantAllOffsetX + bulkHalfW} {permCfg.UserMode.UserActionRowOffsetY + bulkHalfH}",
                        },
                        Text =
                        {
                            Text = Msg("PERM_GRANT_ALL", player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = permCfg.BulkActions.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.BulkActions.TextColor,
                        },
                    },
                    LayerContentBody,
                    "Perm_GrantAllUserBtn"
                );

                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command =
                                $"radminmenu.perm_revoke_all_user {targetUserId} {session.PermSelectedPlugin}",
                            Color = permCfg.BulkActions.RevokeAllColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{permCfg.BulkActions.RevokeAllOffsetX - bulkHalfW} {permCfg.UserMode.UserActionRowOffsetY - bulkHalfH}",
                            OffsetMax =
                                $"{permCfg.BulkActions.RevokeAllOffsetX + bulkHalfW} {permCfg.UserMode.UserActionRowOffsetY + bulkHalfH}",
                        },
                        Text =
                        {
                            Text = Msg("PERM_REVOKE_ALL", player.UserIDString),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = permCfg.BulkActions.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.BulkActions.TextColor,
                        },
                    },
                    LayerContentBody,
                    "Perm_RevokeAllUserBtn"
                );

                List<string> permsList;
                if (!pluginsPerms.TryGetValue(session.PermSelectedPlugin, out permsList))
                    permsList = new List<string>();

                int cols = permCfg.PermGrid.Columns;
                int rows = Mathf.CeilToInt((float)permsList.Count / cols);
                if (rows < 1)
                    rows = 1;

                const int upAutoScrollHalfW = 330;
                const int upAutoScrollHalfH = 134;
                float viewHeight = 268f;

                float totalHeight = Mathf.Max(
                    viewHeight,
                    10f
                        + rows * permCfg.PermGrid.CardHeight
                        + (rows - 1) * permCfg.PermGrid.SpacingY
                        + 10f
                );

                container.Add(
                    new CuiElement
                    {
                        Parent = LayerContentBody,
                        Name = "UserPerm_PermScroll",
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin = $"{-upAutoScrollHalfW} {-84 - upAutoScrollHalfH}",
                                OffsetMax = $"{upAutoScrollHalfW} {-84 + upAutoScrollHalfH}",
                            },
                            new CuiImageComponent
                            {
                                Color = "0 0 0 0",
                            },
                            new CuiRectMask2DComponent(),
                            new CuiScrollViewComponent
                            {
                                Vertical = true,
                                Horizontal = false,
                                Inertia = true,
                                MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                                ScrollSensitivity = SCROLL_SENSITIVITY,
                                ContentTransform = new CuiRectTransformComponent
                                {
                                    AnchorMin = "0 1",
                                    AnchorMax = "1 1",
                                    OffsetMin = $"0 {-totalHeight}",
                                    OffsetMax = "0 0",
                                },
                            },
                        },
                    }
                );

                var userData = permission.GetUserData(targetUserId.ToString());
                var directPermsSet =
                    userData != null && userData.Perms != null
                        ? userData.Perms
                        : new HashSet<string>();

                for (int i = 0; i < permsList.Count; i++)
                {
                    string perm = permsList[i];
                    bool isDirect = directPermsSet.Contains(perm);
                    bool hasFromGroup =
                        !isDirect && permission.UserHasPermission(targetUserId.ToString(), perm);

                    int col = i % cols;
                    int row = i / cols;

                    float px = 5f + col * (permCfg.PermGrid.CardWidth + permCfg.PermGrid.SpacingX);
                    float py = 5f + row * (permCfg.PermGrid.CardHeight + permCfg.PermGrid.SpacingY);

                    string btnColor = isDirect
                        ? permCfg.PermGrid.GrantedColor
                        : (hasFromGroup ? permCfg.Tabs.ActiveColor : permCfg.PermGrid.RevokedColor);
                    string statusBadgeText = isDirect
                        ? $"✔ {Msg("PERM_DIRECT", player.UserIDString)}"
                        : (hasFromGroup ? $"✔ {Msg("PERM_GROUP", player.UserIDString)}" : "✖");
                    string permElemName = $"UserPermToggle_{i}";

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = $"radminmenu.perm_toggle_user_perm {targetUserId} {perm}",
                                Color = btnColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 1",
                                AnchorMax = "0 1",
                                OffsetMin = $"{px} {-py - permCfg.PermGrid.CardHeight}",
                                OffsetMax = $"{px + permCfg.PermGrid.CardWidth} {-py}",
                            },
                            Text =
                            {
                                Text = $"  {perm}",
                                Align = TextAnchor.MiddleLeft,
                                FontSize = permCfg.PermGrid.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.PermGrid.TextColor,
                            },
                        },
                        "UserPerm_PermScroll",
                        permElemName
                    );

                    container.Add(
                        new CuiLabel
                        {
                            Text =
                            {
                                Text = $"{statusBadgeText}  ",
                                Align = TextAnchor.MiddleRight,
                                FontSize = permCfg.PermGrid.FontSize - 1,
                                Font = "robotocondensed-bold.ttf",
                                Color = permCfg.PermGrid.TextColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = "0 0",
                                OffsetMax = "0 0",
                            },
                        },
                        permElemName
                    );
                }
            }
        }

        // CHANGE: Список игроков для выбора при управлении правами пользователя через ScrollView
        private void RenderUserSelectionList(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session,
            PermissionManagerSettings permCfg
        )
        {
            int sHalfW = permCfg.Search.Width / 2;
            int sHalfH = permCfg.Search.Height / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = permCfg.Search.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{permCfg.UserMode.UserSearchOffsetX - sHalfW} {permCfg.GroupsBar.OffsetY - sHalfH}",
                        OffsetMax =
                            $"{permCfg.UserMode.UserSearchOffsetX + sHalfW} {permCfg.GroupsBar.OffsetY + sHalfH}",
                    },
                },
                LayerContentBody,
                "UserSearchBg"
            );

            // CHANGE: Интерактивный плейсхолдер поиска пользователей: отображается когда пусто, исчезает по клику
            bool showUserSearchPlaceholder =
                string.IsNullOrEmpty(session.PermSearch)
                && session.FocusedInput != "user_search";

            if (showUserSearchPlaceholder)
            {
                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = "radminmenu.focus user_search",
                            Color = "0 0 0 0",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = $"{permCfg.Search.PaddingX} 0",
                            OffsetMax = $"{-permCfg.Search.PaddingX} 0",
                        },
                        Text =
                        {
                            Text = Msg("PERM_USER_SEARCH_PLACEHOLDER", player.UserIDString),
                            FontSize = permCfg.Search.FontSize,
                            Font = "robotocondensed-regular.ttf",
                            Align = TextAnchor.MiddleLeft,
                            Color = permCfg.Search.PlaceholderColor,
                        },
                    },
                    "UserSearchBg",
                    "UserSearchPlaceholderBtn"
                );
            }
            else
            {
                container.Add(
                    new CuiElement
                    {
                        Parent = "UserSearchBg",
                        Name = "UserSearchInput",
                        Components =
                        {
                            new CuiInputFieldComponent
                            {
                                Text = session.PermSearch,
                                Command = "radminmenu.perm_search ",
                                Align = TextAnchor.MiddleLeft,
                                FontSize = permCfg.Search.FontSize,
                                Font = "robotocondensed-regular.ttf",
                                Color = permCfg.Search.TextColor,
                                CharsLimit = 32,
                                NeedsKeyboard = true,
                                Autofocus = (session.FocusedInput == "user_search"),
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = $"{permCfg.Search.PaddingX} 0",
                                OffsetMax = $"{-permCfg.Search.PaddingX} 0",
                            },
                        },
                    }
                );
            }

            HashSet<ulong> onlineIds = new HashSet<ulong>();
            foreach (BasePlayer bp in BasePlayer.activePlayerList)
            {
                if (bp != null && bp.IsConnected)
                    onlineIds.Add(bp.userID);
            }

            HashSet<ulong> sleepingIds = new HashSet<ulong>();
            foreach (BasePlayer bp in BasePlayer.sleepingPlayerList)
            {
                if (bp != null)
                    sleepingIds.Add(bp.userID);
            }

            var allPlayers = covalence
                .Players.All.Where(p =>
                    string.IsNullOrEmpty(session.PermSearch)
                    || p.Name.IndexOf(session.PermSearch, StringComparison.OrdinalIgnoreCase) >= 0
                    || p.Id.Contains(session.PermSearch)
                )
                .OrderByDescending(p =>
                {
                    if (ulong.TryParse(p.Id, out ulong uid))
                        return onlineIds.Contains(uid);
                    return p.IsConnected;
                })
                .ThenBy(p => p.Name)
                .ToList();

            int totalLabelHalfW = permCfg.UserMode.UserTotalLabelWidth / 2;
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = Msg("PERM_TOTAL_USERS", player.UserIDString, allPlayers.Count),
                        Align = TextAnchor.MiddleLeft,
                        FontSize = permCfg.UserMode.ItemFontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = permCfg.UserMode.DetailsColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{permCfg.UserMode.UserTotalLabelOffsetX - totalLabelHalfW} {permCfg.GroupsBar.OffsetY - sHalfH}",
                        OffsetMax =
                            $"{permCfg.UserMode.UserTotalLabelOffsetX + totalLabelHalfW} {permCfg.GroupsBar.OffsetY + sHalfH}",
                    },
                },
                LayerContentBody
            );

            int cols = permCfg.PluginGrid.Columns;
            int rows = Mathf.CeilToInt((float)allPlayers.Count / cols);
            if (rows < 1)
                rows = 1;

            const int userSelScrollHalfW = 330;
            const int userSelScrollHalfH = 170;
            float userSelViewHeight = 340f;

            float totalHeight = Mathf.Max(
                userSelViewHeight,
                10f
                    + rows * permCfg.UserMode.UserPlayerCardHeight
                    + (rows - 1) * permCfg.PluginGrid.SpacingY
                    + 10f
            );

            container.Add(
                new CuiElement
                {
                    Parent = LayerContentBody,
                    Name = "Perm_UserSelectScroll",
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{-userSelScrollHalfW} {-45 - userSelScrollHalfH}",
                            OffsetMax = $"{userSelScrollHalfW} {-45 + userSelScrollHalfH}",
                        },
                        new CuiImageComponent
                        {
                            Color = "0 0 0 0",
                        },
                        new CuiRectMask2DComponent(),
                        new CuiScrollViewComponent
                        {
                            Vertical = true,
                            Horizontal = false,
                            Inertia = true,
                            MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                            ScrollSensitivity = SCROLL_SENSITIVITY,
                            ContentTransform = new CuiRectTransformComponent
                            {
                                AnchorMin = "0 1",
                                AnchorMax = "1 1",
                                OffsetMin = $"0 {-totalHeight}",
                                OffsetMax = "0 0",
                            },
                        },
                    },
                }
            );

            for (int i = 0; i < allPlayers.Count; i++)
            {
                var p = allPlayers[i];
                int col = i % cols;
                int row = i / cols;

                float px =
                    5f + col * (permCfg.UserMode.UserPlayerCardWidth + permCfg.PluginGrid.SpacingX);
                float py =
                    5f
                    + row * (permCfg.UserMode.UserPlayerCardHeight + permCfg.PluginGrid.SpacingY);

                // CHANGE: Определение статуса игрока через O(1) хэш-таблицы
                ulong pUid;
                bool hasPUid = ulong.TryParse(p.Id, out pUid);
                bool isOnline = hasPUid ? onlineIds.Contains(pUid) : p.IsConnected;
                bool isSleeping = !isOnline && hasPUid && sleepingIds.Contains(pUid);
                string statusColor = isOnline
                    ? permCfg.BulkActions.GrantAllColor
                    : (
                        isSleeping
                            ? permCfg.BulkActions.BackButtonColor
                            : permCfg.UserMode.DetailsColor
                    );
                string statusText = isOnline
                    ? "● ONLINE"
                    : (isSleeping ? "◐ SLEEPING" : "○ OFFLINE");
                string cardName = $"PlayerPermCard_{p.Id}";

                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = $"radminmenu.perm_select_user {p.Id}",
                            Color = permCfg.UserMode.CardBackgroundColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 1",
                            AnchorMax = "0 1",
                            OffsetMin = $"{px} {-py - permCfg.UserMode.UserPlayerCardHeight}",
                            OffsetMax = $"{px + permCfg.UserMode.UserPlayerCardWidth} {-py}",
                        },
                        Text =
                        {
                            Text =
                                $"  <b>{p.Name}</b>\n  <size=10><color={statusColor}>{statusText}</color></size>",
                            Align = TextAnchor.MiddleLeft,
                            FontSize = permCfg.UserMode.ItemFontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = permCfg.Tabs.TextColor,
                        },
                    },
                    "Perm_UserSelectScroll",
                    cardName
                );
            }
        }

        #endregion

        #region Content Page: Plugin Manager (Reactive & Fast)

        private void RenderPluginManagerContent(
            CuiElementContainer container,
            BasePlayer player,
            AdminSession session
        )
        {
            var pmCfg = _config.PluginManager;

            // 1. Поле поиска
            int sHalfW = pmCfg.Search.Width / 2;
            int sHalfH = pmCfg.Search.Height / 2;

            container.Add(
                new CuiPanel
                {
                    Image = { Color = pmCfg.Search.BackgroundColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{pmCfg.Search.OffsetX - sHalfW} {pmCfg.Search.OffsetY - sHalfH}",
                        OffsetMax =
                            $"{pmCfg.Search.OffsetX + sHalfW} {pmCfg.Search.OffsetY + sHalfH}",
                    },
                },
                LayerContentBody,
                "PmSearchBg"
            );

            // CHANGE: Интерактивный плейсхолдер поиска плагинов: отображается когда пусто, исчезает по клику
            bool showPmPlaceholder =
                string.IsNullOrEmpty(session.PluginSearch) && session.FocusedInput != "pm_search";

            if (showPmPlaceholder)
            {
                container.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Command = "radminmenu.focus pm_search",
                            Color = "0 0 0 0",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = $"{pmCfg.Search.PaddingX} 0",
                            OffsetMax = $"{-pmCfg.Search.PaddingX} 0",
                        },
                        Text =
                        {
                            Text = Msg("PM_SEARCH_PLACEHOLDER", player.UserIDString),
                            FontSize = pmCfg.Search.FontSize,
                            Font = "robotocondensed-regular.ttf",
                            Align = TextAnchor.MiddleLeft,
                            Color = pmCfg.Search.PlaceholderColor,
                        },
                    },
                    "PmSearchBg",
                    "PmSearchPlaceholderBtn"
                );
            }
            else
            {
                container.Add(
                    new CuiElement
                    {
                        Parent = "PmSearchBg",
                        Name = "PmSearchInput",
                        Components =
                        {
                            new CuiInputFieldComponent
                            {
                                Text = session.PluginSearch,
                                Command = "radminmenu.pm_search ",
                                Align = TextAnchor.MiddleLeft,
                                FontSize = pmCfg.Search.FontSize,
                                Font = "robotocondensed-regular.ttf",
                                Color = pmCfg.Search.TextColor,
                                CharsLimit = 32,
                                NeedsKeyboard = true,
                                Autofocus = (session.FocusedInput == "pm_search"),
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = $"{pmCfg.Search.PaddingX} 0",
                                OffsetMax = $"{-pmCfg.Search.PaddingX} 0",
                            },
                        },
                    }
                );
            }

            // 2. Кнопка "Перезагрузить все"
            int rHalfW = pmCfg.ReloadAllButton.Width / 2;
            int rHalfH = pmCfg.ReloadAllButton.Height / 2;

            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.pm_reloadall",
                        Color = pmCfg.ReloadAllButton.BackgroundColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{pmCfg.ReloadAllButton.OffsetX - rHalfW} {pmCfg.ReloadAllButton.OffsetY - rHalfH}",
                        OffsetMax =
                            $"{pmCfg.ReloadAllButton.OffsetX + rHalfW} {pmCfg.ReloadAllButton.OffsetY + rHalfH}",
                    },
                    Text =
                    {
                        Text = Msg("PLUGINS_RELOAD_ALL", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = pmCfg.ReloadAllButton.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = pmCfg.ReloadAllButton.TextColor,
                    },
                },
                LayerContentBody,
                "PmReloadAllBtn"
            );

            // 3. Получение списка плагинов из папки
            var dirInfo = new DirectoryInfo(Interface.Oxide.PluginDirectory);
            FileInfo[] files = dirInfo.Exists ? dirInfo.GetFiles("*.cs") : new FileInfo[0];

            List<string> pluginNames = files
                .Where(f => (f.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                // CHANGE: Сам плагин RAdminMenu исключён из списка управления (нельзя выгрузить/перезагрузить из своей же панели)
                .Where(name => !IsSelfPlugin(name))
                .Where(name =>
                    string.IsNullOrEmpty(session.PluginSearch)
                    || name.IndexOf(session.PluginSearch, StringComparison.OrdinalIgnoreCase) >= 0
                )
                .OrderByDescending(name => _config.General.FavoritePlugins.Contains(name))
                .ThenBy(name => name)
                .ToList();

            float viewHeight = 435f;
            float totalHeight = Mathf.Max(
                viewHeight,
                10f + pluginNames.Count * (pmCfg.Row.Height + pmCfg.Row.Spacing)
            );

            int pmScrollHalfW = 330;
            int pmScrollHalfH = 217;

            container.Add(
                new CuiElement
                {
                    Parent = LayerContentBody,
                    Name = "PluginManager_Scroll",
                    Components =
                    {
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{-pmScrollHalfW} {-27 - pmScrollHalfH}",
                            OffsetMax = $"{pmScrollHalfW} {-27 + pmScrollHalfH}",
                        },
                        new CuiImageComponent
                        {
                            Color = "0 0 0 0",
                        },
                        new CuiRectMask2DComponent(),
                        new CuiScrollViewComponent
                        {
                            Vertical = true,
                            Horizontal = false,
                            Inertia = true,
                            MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                            ScrollSensitivity = SCROLL_SENSITIVITY,
                            ContentTransform = new CuiRectTransformComponent
                            {
                                AnchorMin = "0 1",
                                AnchorMax = "1 1",
                                OffsetMin = $"0 {-totalHeight}",
                                OffsetMax = "0 0",
                            },
                        },
                    },
                }
            );

            // 4. Отрисовка строк плагинов внутри ScrollView
            for (int i = 0; i < pluginNames.Count; i++)
            {
                string pluginName = pluginNames[i];
                Plugin loadedPlugin = plugins.Find(pluginName);
                bool isLoaded = (loadedPlugin != null && loadedPlugin.IsLoaded);
                bool isFavorite = _config.General.FavoritePlugins.Contains(pluginName);

                float yPos = 5f + i * (pmCfg.Row.Height + pmCfg.Row.Spacing);
                string rowName = $"PmRow_{pluginName}";

                int rowHalfW = pmCfg.Row.Width / 2;

                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = pmCfg.Row.BackgroundColor },
                        RectTransform =
                        {
                            AnchorMin = "0.5 1",
                            AnchorMax = "0.5 1",
                            OffsetMin = $"{-rowHalfW} {-yPos - pmCfg.Row.Height}",
                            OffsetMax = $"{rowHalfW} {-yPos}",
                        },
                    },
                    "PluginManager_Scroll",
                    rowName
                );

                // Кнопка избранного ★
                string favColor = isFavorite
                    ? pmCfg.ReloadAllButton.BackgroundColor
                    : pmCfg.Row.DescColor;
                container.Add(
                    new CuiButton
                    {
                        Button = { Command = $"radminmenu.pm_fav {pluginName}", Color = "0 0 0 0" },
                        RectTransform =
                        {
                            AnchorMin = "0 0.5",
                            AnchorMax = "0 0.5",
                            OffsetMin = "8 -12",
                            OffsetMax = "30 12",
                        },
                        Text =
                        {
                            Text = isFavorite ? "★" : "☆",
                            Align = TextAnchor.MiddleCenter,
                            FontSize = 18,
                            Color = favColor,
                        },
                    },
                    rowName
                );

                // Название плагина и описание
                string displayName =
                    loadedPlugin != null
                        ? $"{loadedPlugin.Name} v{loadedPlugin.Version}"
                        : pluginName;
                string desc =
                    loadedPlugin != null && !string.IsNullOrEmpty(loadedPlugin.Description)
                        ? loadedPlugin.Description
                        : "";

                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text =
                                $"<b>{displayName}</b>\n<size={pmCfg.Row.DescFontSize}><color={pmCfg.Row.DescColor}>{desc}</color></size>",
                            Align = TextAnchor.MiddleLeft,
                            FontSize = pmCfg.Row.TitleFontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = pmCfg.Row.TitleColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0.55 1",
                            OffsetMin = "36 0",
                            OffsetMax = "0 0",
                        },
                    },
                    rowName
                );

                // Бейдж статуса (Загружен / Выгружен)
                string badgeText = isLoaded
                    ? Msg("STATUS_LOADED", player.UserIDString)
                    : Msg("STATUS_UNLOADED", player.UserIDString);
                string badgeColor = isLoaded
                    ? pmCfg.StatusBadge.LoadedColor
                    : pmCfg.StatusBadge.UnloadedColor;

                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = badgeColor },
                        RectTransform =
                        {
                            AnchorMin = "0.60 0.5",
                            AnchorMax = "0.60 0.5",
                            OffsetMin =
                                $"{-pmCfg.StatusBadge.Width / 2} {-pmCfg.StatusBadge.Height / 2}",
                            OffsetMax =
                                $"{pmCfg.StatusBadge.Width / 2} {pmCfg.StatusBadge.Height / 2}",
                        },
                    },
                    rowName,
                    $"{rowName}_Badge"
                );

                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = badgeText,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = pmCfg.StatusBadge.FontSize,
                            Font = "robotocondensed-bold.ttf",
                            Color = pmCfg.StatusBadge.TextColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = "0 0",
                            OffsetMax = "0 0",
                        },
                    },
                    $"{rowName}_Badge"
                );

                int btn1Right = 10;
                int btn1Left = btn1Right + pmCfg.ActionButtons.Width;
                int btn2Right = btn1Left + pmCfg.ActionButtons.Spacing;
                int btn2Left = btn2Right + pmCfg.ActionButtons.Width;
                int actBtnHalfH = pmCfg.ActionButtons.Height / 2;

                if (isLoaded)
                {
                    // Кнопка: Выгрузить
                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = $"radminmenu.pm_unload {pluginName}",
                                Color = pmCfg.ActionButtons.UnloadColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "1 0.5",
                                AnchorMax = "1 0.5",
                                OffsetMin = $"{-btn1Left} {-actBtnHalfH}",
                                OffsetMax = $"{-btn1Right} {actBtnHalfH}",
                            },
                            Text =
                            {
                                Text = Msg("PLUGINS_UNLOAD", player.UserIDString),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = pmCfg.ActionButtons.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = pmCfg.ActionButtons.TextColor,
                            },
                        },
                        rowName
                    );

                    // Кнопка: Перезагрузить
                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = $"radminmenu.pm_reload {pluginName}",
                                Color = pmCfg.ActionButtons.ReloadColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "1 0.5",
                                AnchorMax = "1 0.5",
                                OffsetMin = $"{-btn2Left} {-actBtnHalfH}",
                                OffsetMax = $"{-btn2Right} {actBtnHalfH}",
                            },
                            Text =
                            {
                                Text = Msg("PLUGINS_RELOAD", player.UserIDString),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = pmCfg.ActionButtons.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = pmCfg.ActionButtons.TextColor,
                            },
                        },
                        rowName
                    );
                }
                else
                {
                    // Кнопка: Загрузить
                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = $"radminmenu.pm_load {pluginName}",
                                Color = pmCfg.ActionButtons.LoadColor,
                            },
                            RectTransform =
                            {
                                AnchorMin = "1 0.5",
                                AnchorMax = "1 0.5",
                                OffsetMin = $"{-btn1Left} {-actBtnHalfH}",
                                OffsetMax = $"{-btn1Right} {actBtnHalfH}",
                            },
                            Text =
                            {
                                Text = Msg("PLUGINS_LOAD", player.UserIDString),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = pmCfg.ActionButtons.FontSize,
                                Font = "robotocondensed-bold.ttf",
                                Color = pmCfg.ActionButtons.TextColor,
                            },
                        },
                        rowName
                    );
                }
            }
        }

        #endregion

        #region Modal Dialogs

        private void RenderModal(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, LayerModal);

            AdminSession session = GetSession(player);
            if (string.IsNullOrEmpty(session.ModalType))
                return;

            var mCfg = _config.Modal;
            var container = new CuiElementContainer();

            // Фон блокировки
            container.Add(
                new CuiPanel
                {
                    Image = { Color = mCfg.OverlayColor },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin = "0 0",
                        OffsetMax = "0 0",
                    },
                    CursorEnabled = true,
                },
                "Overlay",
                LayerModal
            );

            // Окно модала (Рамка и Фон)
            int halfW = mCfg.Panel.Width / 2;
            int halfH = mCfg.Panel.Height / 2;

            // CHANGE: Рамка модального окна из конфигурации BorderColor и BorderSize
            container.Add(
                new CuiPanel
                {
                    Image = { Color = mCfg.BorderColor },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin =
                            $"{mCfg.Panel.OffsetX - halfW - mCfg.BorderSize} {mCfg.Panel.OffsetY - halfH - mCfg.BorderSize}",
                        OffsetMax =
                            $"{mCfg.Panel.OffsetX + halfW + mCfg.BorderSize} {mCfg.Panel.OffsetY + halfH + mCfg.BorderSize}",
                    },
                },
                LayerModal,
                "ModalBorder"
            );

            container.Add(
                new CuiElement
                {
                    Parent = LayerModal,
                    Name = "ModalDialog",
                    Components =
                    {
                        new CuiImageComponent { Color = mCfg.BackgroundColor },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin =
                                $"{mCfg.Panel.OffsetX - halfW} {mCfg.Panel.OffsetY - halfH}",
                            OffsetMax =
                                $"{mCfg.Panel.OffsetX + halfW} {mCfg.Panel.OffsetY + halfH}",
                        },
                        new CuiNeedsKeyboardComponent(),
                    },
                }
            );

            string title = "";

            // CHANGE: Получение титула группы с сохранением точного регистра букв (VIP, ADMIN и т.д.)
            string targetGroupTitle = permission.GetGroupTitle(session.PermTargetName);
            string targetGroupDisplay = !string.IsNullOrWhiteSpace(targetGroupTitle)
                ? targetGroupTitle
                : session.PermTargetName;

            switch (session.ModalType)
            {
                case "creategroup":
                    title = Msg("PERM_CREATE_GROUP", player.UserIDString);
                    break;
                case "clonegroup":
                    title = Msg("MODAL_CLONE_GROUP_TITLE", player.UserIDString, targetGroupDisplay);
                    break;
                case "deletegroup":
                    title = Msg(
                        "MODAL_DELETE_GROUP_TITLE",
                        player.UserIDString,
                        targetGroupDisplay
                    );
                    break;
                // CHANGE: Модал подтверждения опасного действия над игроком
                case "confirm_action":
                    title = Msg("MODAL_CONFIRM_ACTION_TITLE", player.UserIDString);
                    break;
            }

            // Заголовок
            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = title,
                        Align = TextAnchor.MiddleCenter,
                        FontSize = mCfg.Title.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = mCfg.Title.TextColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "1 1",
                        OffsetMin =
                            $"{mCfg.Title.PaddingX} {mCfg.Title.OffsetY - mCfg.Title.Height / 2}",
                        OffsetMax =
                            $"{-mCfg.Title.PaddingX} {mCfg.Title.OffsetY + mCfg.Title.Height / 2}",
                    },
                },
                "ModalDialog"
            );

            // Поле ввода или текст подтверждения
            int inputHalfW = mCfg.Input.Width / 2;
            int inputHalfH = mCfg.Input.Height / 2;

            if (session.ModalType == "deletegroup" || session.ModalType == "confirm_action")
            {
                // CHANGE: Текст подтверждения опасного действия с именем и SteamID цели
                string confirmDesc;
                if (session.ModalType == "confirm_action")
                {
                    string targetDisplay =
                        session.SelectedUserId != 0
                            ? $"{BasePlayer.FindAwakeOrSleeping(session.SelectedUserId.ToString())?.displayName ?? session.SelectedUserId.ToString()} ({session.SelectedUserId})"
                            : "";
                    string actionKey =
                        session.PendingAction == "kill" ? "MODAL_CONFIRM_KILL_DESC"
                        : session.PendingAction == "strip_inv" ? "MODAL_CONFIRM_STRIP_DESC"
                        : session.PendingAction == "revoke_bp" ? "MODAL_CONFIRM_REVOKE_DESC"
                        : "MODAL_CONFIRM_REVOKE_GRANTED_DESC";
                    confirmDesc = Msg(actionKey, player.UserIDString, targetDisplay);
                }
                else
                {
                    confirmDesc = Msg(
                        "MODAL_DELETE_GROUP_DESC",
                        player.UserIDString,
                        targetGroupDisplay
                    );
                }

                // CHANGE: Описание подтверждения удаления группы с точным регистром названия группы
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = confirmDesc,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = mCfg.Input.FontSize,
                            Font = "robotocondensed-regular.ttf",
                            Color = mCfg.Input.DescriptionTextColor,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{-inputHalfW} {-inputHalfH}",
                            OffsetMax = $"{inputHalfW} {inputHalfH}",
                        },
                    },
                    "ModalDialog"
                );
            }
            else
            {
                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = mCfg.Input.BackgroundColor },
                        RectTransform =
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = $"{-inputHalfW} {-inputHalfH}",
                            OffsetMax = $"{inputHalfW} {inputHalfH}",
                        },
                    },
                    "ModalDialog",
                    "ModalInputBg"
                );

                // CHANGE: Интерактивный плейсхолдер ввода модального окна: отображается когда пусто, исчезает по клику
                bool showModalPlaceholder =
                    string.IsNullOrEmpty(session.ModalInput) && session.FocusedInput != "modal_input";

                if (showModalPlaceholder)
                {
                    string placeholderText =
                        session.ModalType == "clonegroup" || session.ModalType == "creategroup"
                            ? Msg("MODAL_GROUP_NAME", player.UserIDString)
                            : Msg("MODAL_PLACEHOLDER_DEFAULT", player.UserIDString);

                    container.Add(
                        new CuiButton
                        {
                            Button =
                            {
                                Command = "radminmenu.focus modal_input",
                                Color = "0 0 0 0",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = $"{mCfg.Input.PaddingX} 0",
                                OffsetMax = $"{-mCfg.Input.PaddingX} 0",
                            },
                            Text =
                            {
                                Text = placeholderText,
                                FontSize = mCfg.Input.FontSize,
                                Font = "robotocondensed-regular.ttf",
                                Align = TextAnchor.MiddleLeft,
                                Color = mCfg.Input.PlaceholderColor,
                            },
                        },
                        "ModalInputBg",
                        "ModalInputPlaceholderBtn"
                    );
                }
                else
                {
                    container.Add(
                        new CuiElement
                        {
                            Parent = "ModalInputBg",
                            Name = "ModalInputField",
                            Components =
                            {
                                new CuiInputFieldComponent
                                {
                                    Text = session.ModalInput,
                                    Command = "radminmenu.modal_input ",
                                    Align = TextAnchor.MiddleLeft,
                                    FontSize = mCfg.Input.FontSize,
                                    Font = "robotocondensed-regular.ttf",
                                    Color = mCfg.Input.TextColor,
                                    CharsLimit = mCfg.Input.CharsLimit,
                                    NeedsKeyboard = true,
                                    Autofocus = true,
                                },
                                new CuiRectTransformComponent
                                {
                                    AnchorMin = "0 0",
                                    AnchorMax = "1 1",
                                    OffsetMin = $"{mCfg.Input.PaddingX} 0",
                                    OffsetMax = $"{-mCfg.Input.PaddingX} 0",
                                },
                            },
                        }
                    );
                }
            }

            // CHANGE: Кнопки подтверждения и отмены с полной шириной ButtonWidth (без переноса текста);
            // для confirm_action подтверждение выполняет конкретное опасное действие напрямую
            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command =
                            session.ModalType == "confirm_action"
                                ? "radminmenu.act_confirmed"
                                : "radminmenu.modal_confirm",
                        Color = mCfg.Buttons.ConfirmColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0",
                        AnchorMax = "0.5 0",
                        OffsetMin =
                            $"{-mCfg.Buttons.Width - mCfg.Buttons.SpacingX / 2} {mCfg.Buttons.OffsetY}",
                        OffsetMax =
                            $"{-mCfg.Buttons.SpacingX / 2} {mCfg.Buttons.OffsetY + mCfg.Buttons.Height}",
                    },
                    Text =
                    {
                        Text = Msg("MODAL_CONFIRM", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = mCfg.Buttons.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = mCfg.Buttons.TextColor,
                    },
                },
                "ModalDialog"
            );

            // Кнопка отмены
            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Command = "radminmenu.modal_cancel",
                        Color = mCfg.Buttons.CancelColor,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0",
                        AnchorMax = "0.5 0",
                        OffsetMin = $"{mCfg.Buttons.SpacingX / 2} {mCfg.Buttons.OffsetY}",
                        OffsetMax =
                            $"{mCfg.Buttons.Width + mCfg.Buttons.SpacingX / 2} {mCfg.Buttons.OffsetY + mCfg.Buttons.Height}",
                    },
                    Text =
                    {
                        Text = Msg("MODAL_CANCEL", player.UserIDString),
                        Align = TextAnchor.MiddleCenter,
                        FontSize = mCfg.Buttons.FontSize,
                        Font = "robotocondensed-bold.ttf",
                        Color = mCfg.Buttons.TextColor,
                    },
                },
                "ModalDialog"
            );

            CuiHelper.AddUi(player, container);
        }

        #endregion

        #region Console Command Dispatcher

        [ConsoleCommand("radminmenu.nav")]
        private void Cmd_Nav(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string category = arg.GetString(0, "quickmenu");
            AdminSession session = GetSession(player);
            session.CurrentCategory = category;
            // CHANGE: Сброс фокуса ввода при переключении разделов
            session.FocusedInput = "";

            // CHANGE: Плавное обновление кнопок навигации, заголовка и контента без пересоздания фоновых панелей
            RenderNavigationButtons(player);
            UpdateHeaderTitleOnly(player);
            RenderContent(player);
        }

        // CHANGE: Команда фокуса ввода для мгновенного скрытия плейсхолдера и открытия клавиатуры
        [ConsoleCommand("radminmenu.focus")]
        private void Cmd_FocusInput(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string targetInput = arg.GetString(0, "");
            AdminSession session = GetSession(player);
            session.FocusedInput = targetInput;

            if (targetInput == "modal_input")
            {
                RenderModal(player);
            }
            else
            {
                RenderContent(player);
            }
        }

        [ConsoleCommand("radminmenu.close")]
        private void Cmd_Close(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;
            DestroyMenu(player);
        }

        [ConsoleCommand("radminmenu.qm")]
        private void Cmd_QuickMenuAction(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string action = arg.GetString(0, "");
            AdminSession session = GetSession(player);

            switch (action)
            {
                case "tp_death":
                    if (player.ServerCurrentDeathNote != null)
                        player.Teleport(player.ServerCurrentDeathNote.worldPosition);
                    break;
                case "tp_spawn":
                    var spawn = ServerMgr.FindSpawnPoint(player);
                    if (spawn != null)
                        player.Teleport(spawn.pos);
                    break;
                case "tp_marker":
                    if (_tpMarkerPlayers.Contains(player.userID))
                    {
                        _tpMarkerPlayers.Remove(player.userID);
                        session.TeleportToMarkerEnabled = false;
                    }
                    else
                    {
                        _tpMarkerPlayers.Add(player.userID);
                        session.TeleportToMarkerEnabled = true;
                    }
                    // CHANGE: Точечное обновление кнопки вместо полной перерисовки — фикс сброса скролла
                    RefreshQuickMenuCardButton(
                        player,
                        "QM_Card_Teleport",
                        _config.QuickMenu.TeleportCard.ButtonWidth,
                        _config.QuickMenu.TeleportCard.ButtonHeight,
                        _config.QuickMenu.TeleportCard.ButtonSpacingX,
                        _config.QuickMenu.TeleportCard.ButtonFontSize,
                        _config.QuickMenu.TeleportCard.ButtonTextColor,
                        0,
                        Msg("QM_TP_MARKER", player.UserIDString),
                        "radminmenu.qm tp_marker",
                        _tpMarkerPlayers.Contains(player.userID)
                            ? _config.QuickMenu.TeleportCard.ButtonActiveColor
                            : _config.QuickMenu.TeleportCard.ButtonDefaultColor
                    );
                    break;
                case "heal":
                    if (player.IsWounded())
                        player.StopWounded();
                    player.Heal(player.MaxHealth());
                    player.metabolism.calories.value = player.metabolism.calories.max;
                    player.metabolism.hydration.value = player.metabolism.hydration.max;
                    player.metabolism.radiation_level.value = 0;
                    player.metabolism.radiation_poison.value = 0;
                    break;
                // CHANGE: Починка всех предметов в инвентаре игрока
                case "repair":
                    if (player.inventory != null)
                    {
                        var containers = new[]
                        {
                            player.inventory.containerBelt,
                            player.inventory.containerWear,
                            player.inventory.containerMain,
                        };
                        foreach (var c in containers)
                        {
                            if (c?.itemList != null)
                            {
                                foreach (var item in c.itemList)
                                {
                                    if (item != null && item.hasCondition)
                                    {
                                        item.maxCondition = item.info.condition.max;
                                        item.condition = item.maxCondition;
                                        item.MarkDirty();
                                    }
                                }
                            }
                        }
                    }
                    break;
                // CHANGE: Очистка инвентаря игрока
                case "clear_inv":
                    player.inventory?.Strip();
                    break;
                // CHANGE: Переключение креатив-режима для самого админа из Быстрого меню
                case "creative":
                    ToggleCreativeMode(player);
                    // CHANGE: Точечное обновление кнопки вместо полной перерисовки — фикс сброса скролла
                    RefreshQuickMenuCardButton(
                        player,
                        "QM_Card_Actions",
                        _config.QuickMenu.ActionsCard.ButtonWidth,
                        _config.QuickMenu.ActionsCard.ButtonHeight,
                        _config.QuickMenu.ActionsCard.ButtonSpacingX,
                        _config.QuickMenu.ActionsCard.ButtonFontSize,
                        _config.QuickMenu.ActionsCard.ButtonTextColor,
                        3,
                        Msg("QM_CREATIVE", player.UserIDString),
                        "radminmenu.qm creative",
                        _creativePlayers.Contains(player.userID)
                            ? _config.QuickMenu.ActionsCard.CreativeActiveButtonColor
                            : _config.QuickMenu.ActionsCard.CreativeButtonColor
                    );
                    break;
                // CHANGE: Быстрые пресеты времени с мгновенным обновлением тикера
                case "time_day":
                    ConVar.Env.time = 12f;
                    session.TimeInput = "12:00";
                    UpdateQuickMenuTimeOnly(player);
                    break;
                case "time_night":
                    ConVar.Env.time = 0f;
                    session.TimeInput = "00:00";
                    UpdateQuickMenuTimeOnly(player);
                    break;
                case "heli":
                    var heli = GameManager.server.CreateEntity(
                        "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab",
                        player.transform.position + new Vector3(0, 50, 0)
                    );
                    heli?.Spawn();
                    break;
                case "bradley":
                    var bradley = GameManager.server.CreateEntity(
                        "assets/prefabs/npc/m2bradley/bradleyapc.prefab",
                        player.transform.position
                    );
                    bradley?.Spawn();
                    break;
                case "cargo":
                    var cargo = GameManager.server.CreateEntity(
                        "assets/content/vehicles/boats/cargoship/cargoshiptest.prefab"
                    );
                    if (cargo != null)
                    {
                        cargo.SendMessage(
                            "TriggeredEventSpawn",
                            SendMessageOptions.DontRequireReceiver
                        );
                        cargo.Spawn();
                    }
                    break;
                // CHANGE: Самолёт и чинук спавнятся напрямую префабами — консольные команды supply.call/spawn
                // не работают при запуске с сервера (требуют контекст игрока/чит-права)
                case "airdrop":
                    var plane = GameManager.server.CreateEntity(
                        "assets/prefabs/npc/cargo plane/cargo_plane.prefab"
                    );
                    plane?.Spawn();
                    break;
                case "chinook":
                    var ch47 = GameManager.server.CreateEntity(
                        "assets/prefabs/npc/ch47/ch47scientists.entity.prefab",
                        player.transform.position + new Vector3(0, 50, 0)
                    );
                    ch47?.Spawn();
                    break;
                // CHANGE: Обработчики быстрых команд погоды в Быстром меню
                case "weather_clear":
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.load Clear");
                    break;
                case "weather_rain":
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.load RainMild");
                    break;
                case "weather_fog":
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.load Fog");
                    break;
                case "weather_storm":
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.load Storm");
                    break;
                case "weather_reset":
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.reset");
                    break;
                // CHANGE: Кнопка "Отчет" удалена — обработчик weather_report убран
            }
        }

        // CHANGE: Универсальное выполнение команд (поддержка чат-команд, серверных и клиентских консольных команд)
        private void ExecuteAdminActionCommand(
            BasePlayer player,
            string cmdToRun,
            bool executeAsServer
        )
        {
            if (player == null || string.IsNullOrWhiteSpace(cmdToRun))
                return;

            string trimmed = cmdToRun.Trim();

            if (executeAsServer)
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, trimmed);
                return;
            }

            if (trimmed.StartsWith("/"))
            {
                rust.RunClientCommand(player, "chat.say", trimmed);
                return;
            }

            player.SendConsoleCommand(trimmed);
        }

        // CHANGE: Обработчики консольных команд для панели управления временем суток (чат-уведомления исключены для предотвращения спама при кликах)

        [ConsoleCommand("radminmenu.qm_time_input")]
        private void Cmd_QuickMenuTimeInput(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.TimeInput = arg.HasArgs(1)
                ? arg.FullString.ToString().Trim()
                : arg.GetString(0, "");
        }

        [ConsoleCommand("radminmenu.qm_time_apply")]
        private void Cmd_QuickMenuTimeApply(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            string input = (session.TimeInput ?? "").Trim();
            if (arg.HasArgs(1))
            {
                input = arg.FullString.ToString().Trim();
            }
            if (string.IsNullOrEmpty(input))
                return;

            input = input.Replace(',', '.');
            float setTime = -1f;
            if (input.Contains(":"))
            {
                string[] parts = input.Split(':');
                if (
                    parts.Length == 2
                    && int.TryParse(parts[0], out int h)
                    && int.TryParse(parts[1], out int m)
                )
                {
                    h = Mathf.Clamp(h, 0, 23);
                    m = Mathf.Clamp(m, 0, 59);
                    setTime = h + (m / 60f);
                }
            }
            else if (
                float.TryParse(
                    input,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float parsed
                )
            )
            {
                setTime = ((parsed % 24f) + 24f) % 24f;
            }

            if (setTime >= 0f)
            {
                ConVar.Env.time = setTime;
                session.TimeInput = FormatTimeHours(setTime);
                // CHANGE: Чат-уведомление исключено: меню обновляется мгновенно, визуальный результат виден в игре
            }

            RenderContent(player);
        }

        // CHANGE: Обработчики команд детального управления погодой (WeatherManager)
        [ConsoleCommand("radminmenu.weather_tab")]
        private void Cmd_WeatherTab(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.WeatherSubCategory = arg.GetString(0, "presets");
            session.FocusedInput = "";
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.weather_preset")]
        private void Cmd_WeatherPreset(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string preset = arg.GetString(0, "Clear");
            if (preset.Equals("Reset", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, "weather.reset");
            }
            // CHANGE: Пресет "Report" удалён вместе с кнопкой отчета
            else
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Server, $"weather.load {preset}");
            }

            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.weather_set")]
        private void Cmd_WeatherSet(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string convar = arg.GetString(0, "");
            string valStr = arg.GetString(1, "-1");
            if (string.IsNullOrEmpty(convar))
                return;

            if (
                float.TryParse(
                    valStr.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float val
                )
            )
            {
                ConsoleSystem.Run(
                    ConsoleSystem.Option.Server,
                    $"{convar} {val.ToString(CultureInfo.InvariantCulture)}"
                );
                AdminSession session = GetSession(player);
                session.WeatherInputs[convar] = val.ToString(
                    "0.##",
                    CultureInfo.InvariantCulture
                );
            }

            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.weather_input")]
        private void Cmd_WeatherInput(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string convar = arg.GetString(0, "");
            string input = arg.HasArgs(2) ? arg.GetString(1, "") : "";
            if (string.IsNullOrEmpty(convar))
                return;

            AdminSession session = GetSession(player);
            session.WeatherInputs[convar] = input.Trim();
        }

        [ConsoleCommand("radminmenu.weather_apply")]
        private void Cmd_WeatherApply(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string convar = arg.GetString(0, "");
            if (string.IsNullOrEmpty(convar))
                return;

            AdminSession session = GetSession(player);
            if (
                session.WeatherInputs.TryGetValue(convar, out string input)
                && !string.IsNullOrEmpty(input)
            )
            {
                if (
                    float.TryParse(
                        input.Replace(',', '.'),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float val
                    )
                )
                {
                    ConsoleSystem.Run(
                        ConsoleSystem.Option.Server,
                        $"{convar} {val.ToString(CultureInfo.InvariantCulture)}"
                    );
                }
            }

            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.pl_filter")]
        private void Cmd_PlayerFilter(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.PlayerFilter = arg.GetString(0, "online");
            session.PlayerPage = 0;
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.pl_search")]
        private void Cmd_PlayerSearch(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            // CHANGE: Корректное преобразование StringView через ToString()
            session.PlayerSearch = arg.FullString.ToString().Trim();
            session.PlayerPage = 0;
            session.FocusedInput = "";
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.pl_page")]
        private void Cmd_PlayerPage(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.PlayerPage = arg.GetInt(0, 0);
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.pl_select")]
        private void Cmd_PlayerSelect(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            ulong targetId = arg.GetULong(0, 0);
            if (targetId == 0)
                return;

            AdminSession session = GetSession(player);
            session.SelectedUserId = targetId;
            session.CurrentCategory = "userinfo";

            // CHANGE: Обновление заголовка и тела карточки игрока без пересоздания фоновых панелей
            UpdateHeaderTitleOnly(player);
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.act")]
        private void Cmd_UserAction(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            if (session.SelectedUserId == 0)
                return;

            BasePlayer target = BasePlayer.FindAwakeOrSleeping(session.SelectedUserId.ToString());
            IPlayer targetIPlayer = covalence.Players.FindPlayerById(
                session.SelectedUserId.ToString()
            );

            string action = arg.GetString(0, "");

            // CHANGE: Опасные действия выполняются только после подтверждения в модальном окне
            if (
                action == "kill"
                || action == "strip_inv"
                || action == "revoke_bp"
                || action == "revoke_bp_granted"
            )
            {
                session.PendingAction = action;
                session.ModalType = "confirm_action";
                session.ModalInput = "";
                RenderModal(player);
                return;
            }

            switch (action)
            {
                case "tp_self_to":
                    if (target != null)
                        player.Teleport(target);
                    break;
                case "tp_to_self":
                    if (target != null)
                    {
                        target.EnsureDismounted();
                        target.Teleport(player);
                    }
                    break;
                case "tp_auth":
                    if (target != null)
                    {
                        var targets = BaseEntity.Util.FindTargetsAuthedTo(
                            target.userID,
                            string.Empty
                        );
                        if (targets.Length > 0)
                            player.Teleport(targets.GetRandom().transform.position);
                    }
                    break;
                case "tp_death":
                    if (target != null && target.ServerCurrentDeathNote != null)
                        player.Teleport(target.ServerCurrentDeathNote.worldPosition);
                    break;
                case "heal_100":
                    if (target != null)
                    {
                        if (target.IsWounded())
                            target.StopWounded();
                        target.Heal(target.MaxHealth());
                        if (target.metabolism != null)
                        {
                            if (target.metabolism.calories != null)
                                target.metabolism.calories.value = target.metabolism.calories.max;
                            if (target.metabolism.hydration != null)
                                target.metabolism.hydration.value = target.metabolism.hydration.max;
                        }
                        target.SendNetworkUpdate();
                        UpdateUserInfoRealtime(player);
                    }
                    break;
                // CHANGE: Действие полного снятия радиации у игрока
                case "clear_rad":
                    if (target != null)
                    {
                        if (target.metabolism != null)
                        {
                            if (target.metabolism.radiation_level != null)
                                target.metabolism.radiation_level.value = 0f;
                            if (target.metabolism.radiation_poison != null)
                                target.metabolism.radiation_poison.value = 0f;
                        }
                        target.SendNetworkUpdate();
                        UpdateUserInfoRealtime(player);
                    }
                    break;
                case "spectate":
                    if (target != null && target.IsConnected)
                    {
                        DestroyMenu(player);
                        player.StartSpectating();
                        player.UpdateSpectateTarget(target.UserIDString);
                    }
                    break;
                // CHANGE: Корректное переключение креатив-режима через нативный флаг CreativeMode и коллекцию _creativePlayers
                case "toggle_creative":
                    if (target != null)
                        ToggleCreativeMode(target);
                    break;
                case "toggle_cuff":
                    if (target != null)
                        ToggleHandcuffs(target);
                    break;
                case "view_inv":
                    if (target != null)
                    {
                        ViewInventory(player, target);
                        return;
                    }
                    break;
                case "view_bp":
                    if (target != null)
                    {
                        Item backpack = GetEquippedBackpack(target);
                        if (backpack == null || backpack.contents == null)
                        {
                            break;
                        }

                        ViewContainer(player, backpack.contents);
                        return;
                    }
                    break;
                // CHANGE: kill/strip_inv/revoke_bp/revoke_bp_granted перехватываются выше — требуют подтверждения
                case "unlock_bp":
                    if (target != null)
                    {
                        var info = target.PersistantPlayerInfo;
                        if (info != null)
                        {
                            // CHANGE: Запоминаем дельту — только те itemid, которые реально добавлены плагином.
                            // Откат удаляет исключительно их, не трогая чертежи, изученные игроком позже за скрап.
                            var granted = new List<int>();
                            var current = new HashSet<int>(info.unlockedItems);
                            foreach (var def in ItemManager.itemList)
                            {
                                if (def.Blueprint == null)
                                    continue;
                                if (current.Add(def.itemid))
                                {
                                    info.unlockedItems.Add(def.itemid);
                                    granted.Add(def.itemid);
                                }
                            }
                            Interface.Oxide.DataFileSystem.WriteObject(
                                $"RSystem/RAdminMenu/Blueprints/{target.userID}",
                                granted
                            );
                            target.PersistantPlayerInfo = info;
                            target.SendNetworkUpdate();
                        }
                    }
                    break;
                // CHANGE: revoke_bp/revoke_bp_granted выполняются в ExecuteDestructiveAction после подтверждения
            }

            RenderContent(player);
        }

        // CHANGE: Исполнитель опасных действий — вызывается только после подтверждения в модальном окне
        // Предусловие: admin HasAccess, target найден по session.SelectedUserId.
        private void ExecuteDestructiveAction(BasePlayer admin, string action, BasePlayer target)
        {
            if (admin == null || target == null)
                return;

            switch (action)
            {
                case "kill":
                    if (!target.IsDead())
                        target.Hurt(target.MaxHealth() * 10, Rust.DamageType.Suicide, admin);
                    break;
                case "strip_inv":
                    target.inventory.Strip();
                    break;
                case "revoke_bp":
                {
                    var info = target.PersistantPlayerInfo;
                    if (info != null)
                    {
                        info.unlockedItems.Clear();
                        foreach (var def in ItemManager.itemList)
                            if (def.Blueprint != null && def.Blueprint.defaultBlueprint)
                                info.unlockedItems.Add(def.itemid);
                        target.PersistantPlayerInfo = info;
                        target.SendNetworkUpdate();
                    }
                    break;
                }
                case "revoke_bp_granted":
                {
                    var info = target.PersistantPlayerInfo;
                    if (info == null)
                        break;

                    // CHANGE: Удаляем только itemid, добавленные плагином (дельта из файла).
                    // Чертежи, изученные игроком самостоятельно до или после выдачи, остаются.
                    string grantedPath = $"RSystem/RAdminMenu/Blueprints/{target.userID}";
                    var granted = Interface.Oxide.DataFileSystem.ExistsDatafile(grantedPath)
                        ? Interface.Oxide.DataFileSystem.ReadObject<List<int>>(grantedPath)
                        : null;
                    if (granted == null || granted.Count == 0)
                    {
                        // CHANGE: Без дельты откат невозможен — выдача выполнялась до добавления этой логики
                        admin.ChatMessage(
                            Msg("ACT_REVOKE_GRANTED_NO_SNAPSHOT", admin.UserIDString)
                        );
                        break;
                    }
                    var toRemove = new HashSet<int>(granted);
                    info.unlockedItems.RemoveAll(id => toRemove.Contains(id));
                    target.PersistantPlayerInfo = info;
                    target.SendNetworkUpdate();

                    // Дельта отработана — файл больше не нужен, чтобы повторный откат не трогал новое
                    // CHANGE: DeleteFile отсутствует в DataFileSystem этой версии Oxide — удаляем напрямую через System.IO
                    System.IO.File.Delete(
                        System.IO.Path.Combine(
                            Interface.Oxide.DataDirectory,
                            grantedPath + ".json"
                        )
                    );
                    break;
                }
            }
        }

        // CHANGE: Выполнение опасного действия после нажатия "Подтвердить" в модальном окне
        [ConsoleCommand("radminmenu.act_confirmed")]
        private void Cmd_UserActionConfirmed(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            if (session.SelectedUserId == 0)
                return;

            string action = session.PendingAction;
            session.PendingAction = "";
            session.ModalType = "";
            CuiHelper.DestroyUi(player, LayerModal);

            if (string.IsNullOrEmpty(action))
                return;

            BasePlayer target = BasePlayer.FindAwakeOrSleeping(
                session.SelectedUserId.ToString()
            );
            if (target == null)
                return;

            ExecuteDestructiveAction(player, action, target);
            RenderContent(player);
        }

        private readonly HashSet<ulong> _cuffedPlayers = new HashSet<ulong>();

        // CHANGE: Проверка надетых наручников
        private bool IsPlayerHandcuffed(BasePlayer target)
        {
            if (target == null || target.inventory == null)
                return false;

            return target.IsRestrained || _cuffedPlayers.Contains(target.userID);
        }

        // CHANGE: Надевание / снятие наручников по официальной нативной механике Rust
        private void ToggleHandcuffs(BasePlayer target)
        {
            if (target == null || target.inventory == null)
                return;

            bool isCuffed = IsPlayerHandcuffed(target);
            if (isCuffed)
            {
                // Снять наручники
                _cuffedPlayers.Remove(target.userID);

                target.SetPlayerFlag(BasePlayer.PlayerFlags.IsRestrained, false);
                target.restraintItemId = null;
                target.inventory.SetLockedByRestraint(false);

                // Очистить наручники из всех контейнеров
                var containers = new[]
                {
                    target.inventory.containerBelt,
                    target.inventory.containerWear,
                    target.inventory.containerMain,
                };
                foreach (var c in containers)
                {
                    if (c?.itemList != null)
                    {
                        var cuffItems = c
                            .itemList.Where(i =>
                                i.info != null && i.info.shortname.Contains("handcuff")
                            )
                            .ToList();
                        foreach (var cuff in cuffItems)
                        {
                            Handcuffs hc = cuff.GetHeldEntity() as Handcuffs;
                            if (hc != null)
                                hc.SetLocked(false, target, cuff);
                            cuff.RemoveFromContainer();
                            cuff.Remove();
                        }
                    }
                }

                target.SendNetworkUpdateImmediate();
                target.inventory.SendSnapshot();
            }
            else
            {
                // Надеть наручники
                _cuffedPlayers.Add(target.userID);

                target.SetPlayerFlag(BasePlayer.PlayerFlags.IsRestrained, true);
                target.SendNetworkUpdateImmediate();

                Item handcuffsItem = ItemManager.CreateByName("handcuffs", 1);
                if (handcuffsItem != null)
                {
                    handcuffsItem.SetFlag(global::Item.Flag.IsOn, true);
                    if (!handcuffsItem.MoveToContainer(target.inventory.containerBelt))
                    {
                        Item slot0 = target.inventory.containerBelt.GetSlot(0);
                        if (slot0 != null)
                        {
                            if (!slot0.MoveToContainer(target.inventory.containerMain))
                                slot0.DropAndTossUpwards(target.transform.position);
                            handcuffsItem.MoveToContainer(target.inventory.containerBelt);
                        }
                    }

                    handcuffsItem.MarkDirty();
                    target.restraintItemId = handcuffsItem.uid;
                    target.SendConsoleCommand("inventory.activebelt", handcuffsItem.position);
                    target.svActiveItemID = handcuffsItem.uid;
                    target.inventory.SetLockedByRestraint(true);

                    Handcuffs hc = handcuffsItem.GetHeldEntity() as Handcuffs;
                    if (hc != null)
                    {
                        hc.SetLocked(true, target, handcuffsItem);
                    }
                }

                target.SendNetworkUpdateImmediate();
                target.inventory.SendSnapshot();
            }
        }

        // CHANGE: Блокировка переключения и доставания предметов в руки для скованных игроков
        private object OnActiveItemChange(BasePlayer player, Item oldItem, ItemId newItemId)
        {
            if (
                player != null
                && _cuffedPlayers.Contains(player.userID)
                && newItemId != default(ItemId)
            )
            {
                player.svActiveItemID = default(ItemId);
                player.SendNetworkUpdate();
                return false;
            }
            return null;
        }

        // CHANGE: Блокировка атак и стрельбы для скованных игроков
        private object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker != null && _cuffedPlayers.Contains(attacker.userID))
            {
                attacker.svActiveItemID = default(ItemId);
                attacker.SendNetworkUpdate();
                return false;
            }
            return null;
        }

        // CHANGE: Блокировка крафта для скованных игроков в наручниках
        private object CanCraft(ItemCrafter itemCrafter, ItemBlueprint bp, int amount)
        {
            if (
                itemCrafter?.baseEntity != null
                && _cuffedPlayers.Contains(itemCrafter.baseEntity.userID)
            )
                return false;
            return null;
        }

        #region Creative Mode Hooks

        // CHANGE: Единая точка переключения креатив-режима (используется меню действий на игроке и Быстрым меню админа)
        // CHANGE: Дополнительно включает/выключает нативные серверные конвары Creative.*: каждая игровая проверка
        // требует ОДНОВРЕМЕННО флаг игрока и конвар (player.IsInCreativeMode && Creative.freeBuild и т.п.),
        // поэтому флаг без конвар не активирует большую часть креатив-возможностей.
        private void ToggleCreativeMode(BasePlayer target)
        {
            if (target == null)
                return;

            if (!_creativePlayers.Contains(target.userID))
            {
                _creativePlayers.Add(target.userID);
                ApplyCreativeConvars(true);
                target.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, false);
                target.SetPlayerFlag(BasePlayer.PlayerFlags.CreativeMode, true);
                target.Command("debug.setcreative_ui", true);
                uint initialColor = GetPlayerContainerColor(target);
                target.Command("client.SelectedShippingContainerBlockColour", initialColor);
                target.SendNetworkUpdateImmediate();
            }
            else
            {
                _creativePlayers.Remove(target.userID);
                target.SetPlayerFlag(BasePlayer.PlayerFlags.CreativeMode, false);
                target.Command("debug.setcreative_ui", false);
                target.SendNetworkUpdateImmediate();
                RestoreCreativeConvarsIfNoneLeft();
            }
        }

        /// <summary>
        /// Включает нативные конвары Creative.* согласно конфигурации.
        /// Инвариант: конвары без флага CreativeMode у игрока ничего не дают (все проверки игры —
        /// конъюнкция «флаг + конвар»), поэтому глобальное включение безопасно для обычных игроков.
        /// Инвариант: исходные значения конвар снимаются в снапшот перед первым включением и
        /// восстанавливаются при выключении — ручные настройки владельца сервера не затираются.
        /// </summary>
        /// <param name="enabled">true — включить конвары (со снапшотом исходных значений), false — восстановить снапшот.</param>
        private void ApplyCreativeConvars(bool enabled)
        {
            CreativeSettings cfg = _config.Creative;
            if (cfg == null)
                return;

            if (enabled)
            {
                if (!_creativeConvarsSnapshotTaken)
                {
                    _creativeConvarsSnapshotTaken = true;
                    _creativeConvarsSnapshot["creative.freebuild"] = ConVar.Creative.freeBuild;
                    _creativeConvarsSnapshot["creative.freerepair"] = ConVar.Creative.freeRepair;
                    _creativeConvarsSnapshot["creative.freeplacement"] = ConVar.Creative.freePlacement;
                    _creativeConvarsSnapshot["creative.bypassholdtoplaceduration"] = ConVar.Creative.bypassHoldToPlaceDuration;
                    _creativeConvarsSnapshot["creative.unlimitedio"] = ConVar.Creative.unlimitedIo;
                    _creativeConvarsSnapshot["creative.alwaysonenabled"] = ConVar.Creative.alwaysOnEnabled;
                }

                SetServerVar("creative.freebuild", cfg.FreeBuild);
                SetServerVar("creative.freerepair", cfg.FreeRepair);
                SetServerVar("creative.freeplacement", cfg.FreePlacement);
                SetServerVar(
                    "creative.bypassholdtoplaceduration",
                    cfg.BypassHoldToPlaceDuration
                );
                SetServerVar("creative.unlimitedio", cfg.UnlimitedIo);
                SetServerVar("creative.alwaysonenabled", cfg.AlwaysOn);
            }
            else
            {
                if (!_creativeConvarsSnapshotTaken)
                    return;

                foreach (KeyValuePair<string, bool> kv in _creativeConvarsSnapshot)
                    SetServerVar(kv.Key, kv.Value);

                _creativeConvarsSnapshot.Clear();
                _creativeConvarsSnapshotTaken = false;
            }
        }

        /// <summary>
        /// Возвращает конвары Creative.* в выключенное состояние, когда активных креатив-игроков не осталось.
        /// Инвариант: вызывается только после удаления игрока из _creativePlayers.
        /// </summary>
        private void RestoreCreativeConvarsIfNoneLeft()
        {
            if (_creativePlayers.Count == 0)
                ApplyCreativeConvars(false);
        }

        /// <summary>
        /// Устанавливает серверную консольную переменную через консольную систему игры.
        /// Предусловие: имя переменной существует на сервере.
        /// Постусловие: при фактическом изменении значения Command.ValueChanged реплицирует значение
        /// всем подключённым клиентам (прямое присваивание статического поля ConVar.Creative.* репликацию
        /// не триггерит, и клиентский креатив-UI остался бы со старым значением до релога).
        /// Сложность: O(1) по времени и памяти.
        /// </summary>
        /// <param name="name">Полное имя переменной, например "creative.freebuild".</param>
        /// <param name="value">Целевое значение.</param>
        private void SetServerVar(string name, bool value)
        {
            ConsoleSystem.Run(ConsoleSystem.Option.Server, name, value ? "1" : "0");
        }

        // CHANGE: Разрешение постройки чертежом без наличия ресурсов в инвентаре для креатив-режима
        private object CanAffordToPlace(
            BasePlayer player,
            Planner planner,
            Construction construction
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID))
                return true;
            return null;
        }

        // CHANGE: Отмена списания ресурсов при постройке чертежом для креатив-режима
        private object OnPayForPlacement(
            BasePlayer player,
            Planner planner,
            Construction construction
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID))
                return false;
            return null;
        }

        // CHANGE: Разрешение улучшения построек киянкой без наличия ресурсов для креатив-режима
        private object CanAffordUpgrade(
            BasePlayer player,
            BuildingBlock block,
            BuildingGrade.Enum grade,
            ulong skin
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID))
                return true;
            return null;
        }

        private object CanAffordUpgrade(
            BasePlayer player,
            BuildingBlock block,
            BuildingGrade.Enum grade
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID))
                return true;
            return null;
        }

        // CHANGE: Отмена списания ресурсов при улучшении построек киянкой для креатив-режима
        private object OnPayForUpgrade(
            BasePlayer player,
            BuildingBlock block,
            BuildingGrade.Enum grade
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID))
                return false;
            return null;
        }

        private object OnPayForUpgrade(
            BasePlayer player,
            BuildingBlock block,
            ConstructionGrade grade
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID))
                return false;
            return null;
        }

        // CHANGE: Мгновенная бесплатная починка построек и конструкций киянкой для креатив-режима
        private object OnStructureRepair(BaseCombatEntity entity, BasePlayer player)
        {
            if (player != null && _creativePlayers.Contains(player.userID) && entity != null)
            {
                if (entity.health < entity.MaxHealth())
                {
                    entity.Heal(entity.MaxHealth() - entity.health);
                    entity.SendNetworkUpdate();
                }
                return false;
            }
            return null;
        }

        // CHANGE: Бесконечная установка деплоящихся предметов в креатив-режиме
        // CHANGE: Возврат реализован выдачей НОВОГО предмета на следующем тике (а не item.amount++): это не зависит от порядка хуков и момента списания предмета игрой.
        // CHANGE: Если сущность уничтожена в тот же тик (установка отклонена другим плагином, например RCraft, который сам возвращает предмет) — возврат не выполняется, дублирование исключено.
        private void OnItemDeployed(Deployer deployer, BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
                return;

            BasePlayer player = deployer?.GetOwnerPlayer();
            if (player != null && _creativePlayers.Contains(player.userID))
            {
                Item item = deployer.GetItem();
                if (item != null)
                {
                    ItemDefinition itemDef = item.info;
                    ulong itemSkin = item.skin;
                    NextTick(() =>
                    {
                        if (entity.IsDestroyed)
                            return;

                        Item refund = ItemManager.CreateByItemID(
                            itemDef.itemid,
                            1,
                            itemSkin
                        );
                        if (refund != null && !player.inventory.GiveItem(refund))
                        {
                            refund.Remove();
                        }
                    });
                }
            }
        }

        // CHANGE: Определение цвета контейнеров из настроек игрока (баллончик/колесо скинов) или из конфигурации
        private uint GetPlayerContainerColor(BasePlayer player)
        {
            if (player == null)
                return _config.General.DefaultContainerColor > 0 ? _config.General.DefaultContainerColor : 1u;

            uint playerColor = BuildingBlock.GetShippingContainerBlockColourForPlayer(player);
            if (playerColor > 0)
                return playerColor;

            if (_config.General.DefaultContainerColor > 0)
                return _config.General.DefaultContainerColor;

            return 1u;
        }

        // CHANGE: Упреждающая установка цвета контейнеров при размещении до вызова ChangeGradeAndSkin
        private void OnConstructionPlace(
            BaseEntity entity,
            Construction component,
            Construction.Target placement,
            BasePlayer player
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID))
            {
                BuildingBlock block = entity as BuildingBlock;
                if (block != null)
                {
                    uint targetColor = GetPlayerContainerColor(player);
                    block.playerCustomColourToApply = targetColor;
                }
            }
        }

        // CHANGE: Фиксация цвета контейнеров из конфигурации/палитры игрока в NextTick (после отработки ChangeGradeAndSkin) для полного устранения мигания и рандомных цветов
        private void OnEntityBuilt(Planner planner, GameObject gameObject)
        {
            BasePlayer player = planner?.GetOwnerPlayer();
            if (player != null && _creativePlayers.Contains(player.userID))
            {
                BuildingBlock block = gameObject?.GetComponent<BuildingBlock>();
                if (block != null)
                {
                    uint targetColor = GetPlayerContainerColor(player);
                    block.playerCustomColourToApply = targetColor;
                    NextTick(() =>
                    {
                        if (block != null && !block.IsDestroyed)
                        {
                            block.SetCustomColour(targetColor);
                        }
                    });
                }
            }
        }

        // CHANGE: Фиксация цвета контейнеров при улучшении без мигания
        private void OnStructureUpgrade(
            BuildingBlock block,
            BasePlayer player,
            BuildingGrade.Enum grade,
            ulong skin
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID) && block != null)
            {
                uint targetColor = GetPlayerContainerColor(player);
                block.playerCustomColourToApply = targetColor;
                NextTick(() =>
                {
                    if (block != null && !block.IsDestroyed)
                    {
                        block.SetCustomColour(targetColor);
                    }
                });
            }
        }

        private void OnStructureUpgrade(
            BuildingBlock block,
            BasePlayer player,
            BuildingGrade.Enum grade
        )
        {
            if (player != null && _creativePlayers.Contains(player.userID) && block != null)
            {
                uint targetColor = GetPlayerContainerColor(player);
                block.playerCustomColourToApply = targetColor;
                NextTick(() =>
                {
                    if (block != null && !block.IsDestroyed)
                    {
                        block.SetCustomColour(targetColor);
                    }
                });
            }
        }

        #endregion

        // CHANGE: Блокировка перемещения предметов для скованных игроков
        private object CanMoveItem(
            Item item,
            PlayerInventory playerLoot,
            ItemContainerId targetContainer,
            int targetSlot,
            int amount
        )
        {
            if (
                playerLoot?.baseEntity != null
                && _cuffedPlayers.Contains(playerLoot.baseEntity.userID)
            )
                return false;
            return null;
        }

        // CHANGE: Команды управления правами и группами
        [ConsoleCommand("radminmenu.perm_mode")]
        private void Cmd_PermMode(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.PermTargetType = arg.GetString(0, "group");
            session.PermSelectedPlugin = "";
            session.SelectedUserId = 0;
            session.PermPage = 0;
            session.PermSearch = "";
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.perm_select_user")]
        private void Cmd_PermSelectUser(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            ulong uid = arg.GetULong(0, 0);
            AdminSession session = GetSession(player);
            session.SelectedUserId = uid;
            session.PermSelectedPlugin = "";
            session.PermPage = 0;
            session.PermSearch = "";
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.perm_select_target")]
        private void Cmd_PermSelectTarget(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.PermTargetName = arg.GetString(0, "default");
            session.PermSelectedPlugin = "";
            session.PermPage = 0;
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.perm_select_plugin")]
        private void Cmd_PermSelectPlugin(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string pluginName = arg.GetString(0, "").Trim();
            if (
                pluginName == "\"\""
                || pluginName == "''"
                || pluginName == "-"
                || pluginName == "none"
                || pluginName == "back"
                || string.IsNullOrWhiteSpace(pluginName)
            )
                pluginName = "";

            AdminSession session = GetSession(player);
            session.PermSelectedPlugin = pluginName;
            session.PermPage = 0;
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.perm_toggle_group_perm")]
        private void Cmd_PermToggleGroupPerm(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string group = arg.GetString(0, "");
            string perm = arg.GetString(1, "");
            if (!string.IsNullOrEmpty(group) && !string.IsNullOrEmpty(perm))
            {
                if (permission.GroupHasPermission(group, perm))
                    permission.RevokeGroupPermission(group, perm);
                else
                    permission.GrantGroupPermission(group, perm, null);

                RenderContent(player);
            }
        }

        [ConsoleCommand("radminmenu.perm_grant_all_group")]
        private void Cmd_PermGrantAllGroup(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string group = arg.GetString(0, "");
            string pluginName = arg.GetString(1, "");
            var all = GetRegisteredPermissionsByPlugin();
            if (all.TryGetValue(pluginName, out var perms))
            {
                foreach (var p in perms)
                    if (!permission.GroupHasPermission(group, p))
                        permission.GrantGroupPermission(group, p, null);

                RenderContent(player);
            }
        }

        [ConsoleCommand("radminmenu.perm_revoke_all_group")]
        private void Cmd_PermRevokeAllGroup(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string group = arg.GetString(0, "");
            string pluginName = arg.GetString(1, "");
            var all = GetRegisteredPermissionsByPlugin();
            if (all.TryGetValue(pluginName, out var perms))
            {
                foreach (var p in perms)
                    if (permission.GroupHasPermission(group, p))
                        permission.RevokeGroupPermission(group, p);

                RenderContent(player);
            }
        }

        [ConsoleCommand("radminmenu.perm_delete_group")]
        private void Cmd_PermDeleteGroup(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string group = arg.GetString(0, "");
            if (!string.IsNullOrEmpty(group) && group != "default" && group != "admin")
            {
                permission.RemoveGroup(group);
                AdminSession session = GetSession(player);
                session.PermTargetName = "default";
                session.PermSelectedPlugin = "";
                session.PermPage = 0;
                RenderContent(player);
            }
        }

        [ConsoleCommand("radminmenu.perm_toggle_user_group")]
        private void Cmd_PermToggleUserGroup(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string userId = arg.GetString(0, "");
            string group = arg.GetString(1, "");
            if (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(group))
            {
                if (permission.UserHasGroup(userId, group))
                    permission.RemoveUserGroup(userId, group);
                else
                    permission.AddUserGroup(userId, group);

                RenderContent(player);
            }
        }

        // CHANGE: Корректное переключение персонального права игрока (независимо от прав, унаследованных от групп)
        [ConsoleCommand("radminmenu.perm_toggle_user_perm")]
        private void Cmd_PermToggleUserPerm(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string userId = arg.GetString(0, "");
            string perm = arg.GetString(1, "");
            if (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(perm))
            {
                var userData = permission.GetUserData(userId);
                bool hasDirect =
                    userData != null && userData.Perms != null && userData.Perms.Contains(perm);
                if (hasDirect)
                    permission.RevokeUserPermission(userId, perm);
                else
                    permission.GrantUserPermission(userId, perm, null);

                RenderContent(player);
            }
        }

        // CHANGE: Выдача всех персональных прав выбранного плагина игроку
        [ConsoleCommand("radminmenu.perm_grant_all_user")]
        private void Cmd_PermGrantAllUser(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string userId = arg.GetString(0, "");
            string pluginName = arg.GetString(1, "");
            var all = GetRegisteredPermissionsByPlugin();
            if (all.TryGetValue(pluginName, out var perms))
            {
                var userData = permission.GetUserData(userId);
                foreach (var p in perms)
                    if (userData == null || userData.Perms == null || !userData.Perms.Contains(p))
                        permission.GrantUserPermission(userId, p, null);

                RenderContent(player);
            }
        }

        // CHANGE: Снятие всех персональных прав выбранного плагина у игрока
        [ConsoleCommand("radminmenu.perm_revoke_all_user")]
        private void Cmd_PermRevokeAllUser(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string userId = arg.GetString(0, "");
            string pluginName = arg.GetString(1, "");
            var all = GetRegisteredPermissionsByPlugin();
            if (all.TryGetValue(pluginName, out var perms))
            {
                var userData = permission.GetUserData(userId);
                if (userData != null && userData.Perms != null)
                {
                    foreach (var p in perms)
                        if (userData.Perms.Contains(p))
                            permission.RevokeUserPermission(userId, p);
                }

                RenderContent(player);
            }
        }

        [ConsoleCommand("radminmenu.perm_search")]
        private void Cmd_PermSearch(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            // CHANGE: Корректное преобразование StringView через ToString()
            session.PermSearch = arg.FullString.ToString().Trim();
            session.PermPage = 0;
            session.FocusedInput = "";
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.perm_page")]
        private void Cmd_PermPage(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.PermPage = arg.GetInt(0, 0);
            RenderContent(player);
        }

        // CHANGE: Проверка, что имя плагина — сам RAdminMenu (защита от выгрузки/перезагрузки себя из своей панели)
        private bool IsSelfPlugin(string pluginName)
        {
            return string.Equals(pluginName, Name, StringComparison.OrdinalIgnoreCase);
        }

        // CHANGE: Реактивное мгновенное управление плагинами
        [ConsoleCommand("radminmenu.pm_load")]
        private void Cmd_PmLoad(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string pluginName = arg.GetString(0, "");
            if (string.IsNullOrEmpty(pluginName) || IsSelfPlugin(pluginName))
                return;

            Interface.Oxide.LoadPlugin(pluginName);

            // CHANGE: Мгновенное обновление в следующем кадре без перезахода во вкладку
            NextFrame(() =>
            {
                if (player != null && player.IsConnected)
                    RenderContent(player);
            });
        }

        [ConsoleCommand("radminmenu.pm_unload")]
        private void Cmd_PmUnload(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string pluginName = arg.GetString(0, "");
            if (string.IsNullOrEmpty(pluginName) || IsSelfPlugin(pluginName))
                return;

            Interface.Oxide.UnloadPlugin(pluginName);

            // CHANGE: Мгновенное обновление в следующем кадре без перезахода во вкладку
            NextFrame(() =>
            {
                if (player != null && player.IsConnected)
                    RenderContent(player);
            });
        }

        [ConsoleCommand("radminmenu.pm_reload")]
        private void Cmd_PmReload(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string pluginName = arg.GetString(0, "");
            if (string.IsNullOrEmpty(pluginName) || IsSelfPlugin(pluginName))
                return;

            Interface.Oxide.ReloadPlugin(pluginName);

            // CHANGE: Мгновенное обновление в следующем кадре
            NextFrame(() =>
            {
                if (player != null && player.IsConnected)
                    RenderContent(player);
            });
        }

        [ConsoleCommand("radminmenu.pm_reloadall")]
        private void Cmd_PmReloadAll(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

#if CARBON
            Carbon.Community.Runtime.ReloadPlugins();
#else
            Interface.Oxide.ReloadAllPlugins();
#endif
            NextFrame(() =>
            {
                if (player != null && player.IsConnected)
                    RenderContent(player);
            });
        }

        [ConsoleCommand("radminmenu.pm_fav")]
        private void Cmd_PmFav(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            string pluginName = arg.GetString(0, "");
            if (string.IsNullOrEmpty(pluginName))
                return;

            if (_config.General.FavoritePlugins.Contains(pluginName))
                _config.General.FavoritePlugins.Remove(pluginName);
            else
                _config.General.FavoritePlugins.Add(pluginName);

            SaveConfig();
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.pm_search")]
        private void Cmd_PmSearch(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            // CHANGE: Корректное преобразование StringView через ToString()
            session.PluginSearch = arg.FullString.ToString().Trim();
            session.PluginPage = 0;
            session.FocusedInput = "";
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.pm_page")]
        private void Cmd_PmPage(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.PluginPage = arg.GetInt(0, 0);
            RenderContent(player);
        }

        [ConsoleCommand("radminmenu.modal_open")]
        private void Cmd_ModalOpen(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            session.ModalType = arg.GetString(0, "");
            session.ModalInput = "";
            RenderModal(player);
        }

        [ConsoleCommand("radminmenu.modal_input")]
        private void Cmd_ModalInput(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            // CHANGE: Корректное извлечение строки из Facepunch.StringView через ToString()
            session.ModalInput = arg.HasArgs(1)
                ? arg.FullString.ToString().Trim()
                : arg.GetString(0, "");
        }

        [ConsoleCommand("radminmenu.modal_cancel")]
        private void Cmd_ModalCancel(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            AdminSession session = GetSession(player);
            session.ModalType = "";
            // CHANGE: Отмена подтверждения опасного действия сбрасывает ожидающее действие
            session.PendingAction = "";
            CuiHelper.DestroyUi(player, LayerModal);
        }

        [ConsoleCommand("radminmenu.modal_confirm")]
        private void Cmd_ModalConfirm(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasAccess(player))
                return;

            AdminSession session = GetSession(player);
            if (arg.HasArgs(1))
            {
                // CHANGE: Корректное приведение StringView к string через ToString()
                session.ModalInput = arg.FullString.ToString().Trim();
            }

            switch (session.ModalType)
            {
                case "creategroup":
                    if (!string.IsNullOrWhiteSpace(session.ModalInput))
                    {
                        // CHANGE: Сохранение исходного регистра букв названия и титула группы, введенного пользователем
                        string newGroup = session.ModalInput.Trim();
                        if (!permission.GroupExists(newGroup))
                        {
                            permission.CreateGroup(newGroup, newGroup, 0);
                            session.PermTargetName = newGroup;
                            session.PermSelectedPlugin = "";
                            session.PermPage = 0;
                        }
                        else
                        {
                            permission.SetGroupTitle(newGroup, newGroup);
                            session.PermTargetName = newGroup;
                            session.PermSelectedPlugin = "";
                            session.PermPage = 0;
                        }
                    }
                    break;
                case "clonegroup":
                    if (
                        !string.IsNullOrWhiteSpace(session.ModalInput)
                        && !string.IsNullOrEmpty(session.PermTargetName)
                    )
                    {
                        // CHANGE: Сохранение исходного регистра букв названия группы при клонировании
                        string newGroup = session.ModalInput.Trim();
                        if (
                            permission.GroupExists(session.PermTargetName)
                            && !permission.GroupExists(newGroup)
                        )
                        {
                            permission.CreateGroup(newGroup, newGroup, 0);
                            string[] perms = permission.GetGroupPermissions(session.PermTargetName);
                            if (perms != null)
                            {
                                foreach (string p in perms)
                                    permission.GrantGroupPermission(newGroup, p, null);
                            }
                            session.PermTargetName = newGroup;
                            session.PermSelectedPlugin = "";
                            session.PermPage = 0;
                        }
                        else if (permission.GroupExists(newGroup))
                        {
                            permission.SetGroupTitle(newGroup, newGroup);
                            session.PermTargetName = newGroup;
                            session.PermSelectedPlugin = "";
                            session.PermPage = 0;
                        }
                    }
                    break;
                case "deletegroup":
                    // CHANGE: Удаление выбранной группы после подтверждения в модальном окне
                    if (
                        !string.IsNullOrEmpty(session.PermTargetName)
                        && session.PermTargetName != "default"
                        && session.PermTargetName != "admin"
                    )
                    {
                        permission.RemoveGroup(session.PermTargetName);
                        session.PermTargetName = "default";
                        session.PermSelectedPlugin = "";
                        session.PermPage = 0;
                    }
                    break;
            }

            session.ModalType = "";
            CuiHelper.DestroyUi(player, LayerModal);
            RenderContent(player);
        }

        // CHANGE: Получение и группировка всех зарегистрированных прав по плагинам
        private Dictionary<string, List<string>> GetRegisteredPermissionsByPlugin()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            string[] allPerms = permission.GetPermissions();
            if (allPerms != null)
            {
                foreach (string perm in allPerms)
                {
                    if (string.IsNullOrEmpty(perm))
                        continue;

                    int dotIndex = perm.IndexOf('.');
                    string pluginKey = dotIndex > 0 ? perm.Substring(0, dotIndex) : "Core";

                    Plugin p = plugins.Find(pluginKey);
                    string displayName = p != null ? p.Name : pluginKey;

                    if (!result.ContainsKey(displayName))
                        result[displayName] = new List<string>();

                    if (!result[displayName].Contains(perm))
                        result[displayName].Add(perm);
                }
            }

            return result;
        }

        #endregion

        #region Steam Info Web Request

        private void RequestSteamInfo(ulong steamId, Action callback)
        {
            if (_cachedSteamInfo.ContainsKey(steamId))
            {
                callback?.Invoke();
                return;
            }

            webrequest.Enqueue(
                $"https://steamcommunity.com/profiles/{steamId}?xml=1",
                null,
                (code, response) =>
                {
                    if (code != 200 || string.IsNullOrEmpty(response))
                        return;
                    try
                    {
                        var xml = new XmlDocument();
                        xml.LoadXml(response);
                        string location =
                            xml.SelectSingleNode("//location")?.InnerText.Trim() ?? "";
                        string avatarFull =
                            xml.SelectSingleNode("//avatarFull")?.InnerText.Trim() ?? "";
                        string memberSince =
                            xml.SelectSingleNode("//memberSince")?.InnerText.Trim() ?? "";
                        string hours =
                            xml.SelectSingleNode(
                                    "//mostPlayedGames/mostPlayedGame[contains(gameLink, '252490')]/hoursOnRecord"
                                )
                                ?.InnerText.Trim()
                            ?? "";

                        _cachedSteamInfo[steamId] = new SteamInfo
                        {
                            Location = location,
                            Avatars = new[] { "", "", avatarFull },
                            RegistrationDate = memberSince,
                            RustHours = hours,
                        };

                        callback?.Invoke();
                    }
                    catch { }
                },
                this
            );
        }

        #endregion
    }
}
