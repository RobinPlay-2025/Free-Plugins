using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

/*
 * CHANGE: создан плагин RIzolenta — починка предметов изолентой (ducttape)
 * без верстака и ресурсов, добавление изоленты в лут ящиков,
 * консольная команда giveizolenta <steamid> <кол-во>.
 *
 * Инварианты:
 *  - Изолента = предмет с shortname "ducttape" и скином из конфига.
 *  - Ремонт через перетаскивание изоленты НА предмет (перехват CanMoveItem
 *    из PlayerInventory.MoveItem — подтверждено декомпиляцией Assembly-CSharp,
 *    chunk7:20517; иного серверного события drag-onto-item в игре нет).
 *  - Ремонт поднимает condition на RepairPercent% от maxCondition и
 *    одновременно снижает maxCondition на WearPercent% (деградация).
 *  - Чинятся только предметы категорий Weapon / Attire / Tool с condition-системой,
 *    не входящие в чёрный список.
 *  - Изолента появляется в луте только у контейнеров из whitelist при первом
 *    лутании (контейнер помечается — повторного спавна нет).
 *
 * Сложность:
 *  - CanMoveItem: O(1) по времени/памяти (поиск в HashSet/списках константен для игрока).
 *  - OnLootEntity: O(k), k — кол-во добавляемых предметов (<= MaxAmount).
 */

namespace Carbon.Plugins
{
    [Info("RIzolenta", "RustInnovate", "1.0.0")]
    [Description("Починка оружия, брони и инструментов изолентой (ducttape) без верстака и ресурсов")]
    public class RIzolenta : CarbonPlugin
    {
        #region Configuration

        private class Configuration
        {
            [JsonProperty("Список ящиков и бочек где будет появляться изолента")]
            public List<string> LootCrates = new List<string>
            {
                "crate_normal_2",
                "crate_tools",
                "loot_barrel_1",
                "loot_barrel_2"
            };

            [JsonProperty("Чёрный список")]
            public List<string> Blacklist = new List<string> { "rifle.l96" };

            [JsonProperty("Мин кол-во выпадение")]
            public int MinAmount = 1;

            [JsonProperty("Макс кол-во выпадение")]
            public int MaxAmount = 2;

            [JsonProperty("Шанс выпадения")]
            public float DropChance = 50.0f;

            [JsonProperty("Скин изоленты")]
            public ulong TapeSkin = 3811198690UL;

            [JsonProperty("Сколько чинить?")]
            public float RepairPercent = 10.0f;

            [JsonProperty("Сколько снимать хп от максимального хп?")]
            public float WearPercent = 5.0f;
        }

        private Configuration config;

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) LoadDefaultConfig();
            }
            catch (Exception ex)
            {
                // CHANGE: при битом конфиге не затираем файл дефолтами —
                // сохраняем копию рядом и логируем причину, чтобы не терять настройки
                var path = $"{Interface.Oxide.ConfigDirectory}{System.IO.Path.DirectorySeparatorChar}RIzolenta.json";
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Copy(path, path + ".bak", true);
                }
                catch (Exception ioEx)
                {
                    Puts($"Не удалось создать бэкап конфига: {ioEx.Message}");
                }

                Puts($"ОШИБКА чтения конфигурации (файл повреждён или был удалён): {ex.Message}. Дефолты применены, старый файл сохранён как RIzolenta.json.bak");
                LoadDefaultConfig();
            }

            // CHANGE: дедупликация списков конфига — ручные дубли в JSON не сохраняются обратно
            if (config != null)
            {
                config.LootCrates = config.LootCrates?.Distinct().ToList() ?? new List<string>();
                config.Blacklist = config.Blacklist?.Distinct().ToList() ?? new List<string>();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["PlayerNotFound"] = "Player \"{0}\" not found or offline.",
                ["NoPermission"] = "Only admins can use this command.",
                ["Given"] = "Given {0} izolenta to {1}.",
                ["ItemName"] = "Изолента",
                ["Blacklisted"] = "Этот предмет нельзя починить изолентой."
            }, this, "en");
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["PlayerNotFound"] = "Игрок «{0}» не найден или не в сети.",
                ["NoPermission"] = "Эта команда доступна только администраторам.",
                ["Given"] = "Выдано {0} шт. изоленты игроку {1}.",
                ["ItemName"] = "Изолента",
                ["Blacklisted"] = "Этот предмет нельзя починить изолентой."
            }, this, "ru");
        }

        private string Msg(BasePlayer player, string key, params object[] args)
            => string.Format(lang.GetMessage(key, this, player?.UserIDString), args);

        #endregion

        #region Fields

        // CHANGE: shortname изоленты зафиксирован константой по требованию заказчика
        private const string TapeShortname = "ducttape";

        // CHANGE: разрешённые категории предметов к починке (оружие, броня, инструменты)
        private static readonly ItemCategory[] RepairableCategories =
        {
            ItemCategory.Weapon,
            ItemCategory.Attire,
            ItemCategory.Tool
        };

        private HashSet<string> crateLookup;
        private HashSet<string> blacklistLookup;

        #endregion

        #region Lifecycle

        private void Init()
        {
            crateLookup = new HashSet<string>(config.LootCrates ?? new List<string>());
            blacklistLookup = new HashSet<string>(config.Blacklist ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }

        // CHANGE: Unload не требуется — состояние плагина не хранит ничего, что нужно освобождать

        #endregion

        #region Repair (drag duct tape onto item)

        /// <summary>
        /// Перехват перетаскивания предмета внутри инвентаря игрока.
        /// Предусловия: игра вызывает хук для каждого MoveItem (drag&drop) с аргументами
        /// (Item, PlayerInventory, ItemContainerId, int slot, int amount, ItemMoveModifier) —
        /// Assembly-CSharp chunk7:20517; третий аргумент — ID контейнера, НЕ ItemContainer.
        /// Инвариант: если переносимая изолента падает на чинимый предмет —
        /// выполняем ремонт, списываем 1 шт. изоленты и блокируем дефолтный swap.
        /// Постусловие: null — дефолтное поведение игры; иначе — move заблокирован.
        /// </summary>
        private object CanMoveItem(Item item, PlayerInventory playerInventory, ItemContainerId targetContainerId, int targetSlot, int amount, ItemMoveModifier modifier)
        {
            if (item == null || playerInventory == null) return null;
            if (item.info.shortname != TapeShortname || item.skin != config.TapeSkin) return null;

            // CHANGE: резолвим контейнер назначения по ID (игра передаёт ItemContainerId, chunk7:20582)
            var targetContainer = playerInventory.FindContainer(targetContainerId);
            if (targetContainer == null) return null;

            // CHANGE: drop на пустой слот — обычное перемещение, не мешаем
            var target = targetContainer.GetSlot(targetSlot);
            if (target == null || target == item) return null;

            var player = item.GetOwnerPlayer();
            if (player == null) return null;

            // CHANGE: предмет из чёрного списка — сообщаем игроку и блокируем перенос;
            // прочие непочинимые случаи (не та категория / целое) блокируем молча
            if (blacklistLookup.Contains(target.info.shortname))
            {
                player.ChatMessage(Msg(player, "Blacklisted"));
                return true;
            }

            if (!CanRepairItem(target) || target.condition >= target.maxCondition)
                return true;

            // CHANGE: расходуем стак изоленты целиком до полной починки предмета:
            // каждая шт. даёт +RepairPercent% к condition, а износ -WearPercent%
            // списывается ОДИН раз за всю починку (как у верстака), а не за каждую шт. —
            // это стимулирует чиниться большим стаком, а не по 1 шт.
            // Цикл гарантированно конечен: condition растёт и упирается в maxCondition.
            int used = 0;
            while (target.condition < target.maxCondition && item.amount > 0 && used < 100)
            {
                target.condition += target.maxCondition * (config.RepairPercent / 100f);
                item.UseItem(1);
                used++;
            }

            if (used > 0)
                ApplyWear(target);
            return true;
        }

        /// <summary>
        /// Проверка, что предмет вообще подлежит починке изолентой.
        /// Предусловия: target != null.
        /// Инварианты: категория в whitelist, shortname не в чёрном списке,
        /// у предмета есть condition-система.
        /// Сложность: O(1).
        /// </summary>
        private bool CanRepairItem(Item target)
        {
            if (target == null || !target.hasCondition) return false;
            if (blacklistLookup.Contains(target.info.shortname)) return false;
            return RepairableCategories.Contains(target.info.category);
        }

        /// <summary>
        /// Списывает износ за починку: -WearPercent% от maxCondition, один раз за всю починку
        /// (аналог штрафа верстака), независимо от кол-ва использованной изоленты.
        /// Инварианты: condition <= maxCondition; maxCondition >= 1 после износа.
        /// Сложность: O(1).
        /// </summary>
        private void ApplyWear(Item target)
        {
            target.condition -= target.maxCondition * (config.WearPercent / 100f);
            target.maxCondition *= 1f - config.WearPercent / 100f;

            // CHANGE: деградация не должна «убить» предмет — гарантируем минимум 1 HP макс. прочности
            if (target.maxCondition < 1f) target.maxCondition = 1f;
            if (target.condition > target.maxCondition) target.condition = target.maxCondition;
            if (target.condition < 0f) target.condition = 0f;
            target.MarkDirty();
        }

        /// <summary>
        /// Создаёт стопку изоленты с кастомным отображаемым именем «Изолента».
        /// Инвариант: item.text задаётся только предметам, созданным плагином —
        /// ванильный ducttape без имени остаётся без изменений.
        /// Сложность: O(1).
        /// </summary>
        private Item CreateTapeItem(int amount)
        {
            var def = ItemManager.FindItemDefinition(TapeShortname);
            if (def == null) return null;

            var tape = ItemManager.Create(def, amount, config.TapeSkin);
            if (tape == null) return null;

            // CHANGE: item.name — переопределение отображаемого имени предмета
            // (механизм из Airstrike.cs:1352 — сериализуется клиенту); текст из локализации плагина
            tape.name = lang.GetMessage("ItemName", this);
            tape.MarkDirty();
            return tape;
        }

        #endregion

        /// <summary>
        /// Блокирует изучение изоленты на исследовательском столе.
        /// Точка вызова игры: ResearchTable, chunk8:1412 — CallHook("CanResearchItem", player, item);
        /// non-null результат запрещает исследование.
        /// Постусловие: для изоленты — блокировка, для остальных предметов — null (ванильное поведение).
        /// </summary>
        private object CanResearchItem(BasePlayer player, Item targetItem)
        {
            if (targetItem != null && targetItem.info.shortname == TapeShortname)
                return false;
            return null;
        }

        #region Loot

        /// <summary>
        /// Добавляет изоленту в лут контейнеров из whitelist при генерации лута.
        /// Используется OnLootSpawn (LootContainer.SpawnLoot, Assembly-CSharp chunk12:34198):
        /// он срабатывает и у ящиков, и у лоомаемых бочек — в отличие от OnLootEntity,
        /// который вызывается только при открытии контейнера игроком (chunk7:22258),
        /// а бочки игроки не открывают, а разбивают.
        /// Постусловие: return void — ванильный PopulateLoot выполняется после добавления изоленты.
        /// Сложность: O(k), k — кол-во выпавших стопок изоленты.
        /// </summary>
        private void OnLootSpawn(LootContainer container)
        {
            if (container == null || container.inventory == null) return;
            if (!crateLookup.Contains(container.ShortPrefabName)) return;

            if (UnityEngine.Random.Range(0f, 100f) >= config.DropChance) return;

            int count = UnityEngine.Random.Range(config.MinAmount, config.MaxAmount + 1);

            // CHANGE: изолента добавляется до ванильного PopulateLoot —
            // при разбитии бочки падает на землю вместе с остальным лутом
            for (int i = 0; i < count; i++)
            {
                var tape = CreateTapeItem(1);
                if (tape == null) continue;
                if (!tape.MoveToContainer(container.inventory))
                {
                    tape.Remove();
                }
            }
        }

        #endregion

        #region Console command

        /// <summary>
        /// Консольная команда: giveizolenta <steamid|ник> <кол-во>.
        /// Работает из серверной консоли и из F1-консоли игрока.
        /// Из F1 команда доступна только админам (authlevel > 0) — F1 открыта всем клиентам.
        /// Инварианты: игрок ищется по SteamID, точному нику или части ника (без учёта регистра);
        /// выдача только игрокам, находящимся в сети.
        /// </summary>
        [ConsoleCommand("giveizolenta")]
        private void ConsoleGiveIsoTape(ConsoleSystem.Arg arg)
        {
            // CHANGE: вызов из F1-консоли клиента разрешён только админам
            var caller = arg.Player();
            if (caller != null && !caller.IsAdmin)
            {
                arg.ReplyWith(Msg(caller, "NoPermission"));
                return;
            }

            if (arg.Args == null || arg.Args.Length < 2)
            {
                arg.ReplyWith("giveizolenta <steamid|ник> <кол-во>");
                return;
            }

            // CHANGE: arg.Args в текущей сборке — Facepunch.StringView[], читаем через GetString
            string nameOrId = arg.GetString(0);
            if (string.IsNullOrEmpty(nameOrId))
            {
                arg.ReplyWith("giveizolenta <steamid|ник> <кол-во>");
                return;
            }

            if (!int.TryParse(arg.GetString(1), out int amount) || amount <= 0)
            {
                arg.ReplyWith("giveizolenta <steamid|ник> <кол-во>");
                return;
            }

            var target = FindPlayer(nameOrId);
            if (target == null)
            {
                arg.ReplyWith(Msg(caller, "PlayerNotFound", nameOrId));
                return;
            }

            var tape = CreateTapeItem(amount);
            if (tape == null)
            {
                arg.ReplyWith($"Ошибка: предмет с shortname '{TapeShortname}' не найден!");
                return;
            }

            if (!tape.MoveToContainer(target.inventory.containerMain) &&
                !tape.MoveToContainer(target.inventory.containerBelt))
            {
                tape.Drop(target.GetDropPosition(), target.GetDropVelocity());
            }

            // CHANGE: уведомление в консоль о выдаче (без сообщений игроку в чат)
            arg.ReplyWith(Msg(caller, "Given", amount, target.displayName));
        }

        /// <summary>
        /// Поиск игрока в сети по SteamID, точному нику или части ника (без учёта регистра).
        /// Предусловия: nameOrId не пустое.
        /// Постусловие: null — игрок не найден или не в сети.
        /// Сложность: O(n), n — кол-во игроков в сети (только при неполном совпадении).
        /// </summary>
        private static BasePlayer FindPlayer(string nameOrId)
        {
            // CHANGE: BasePlayer.Find матчит и SteamID, и точный ник
            var exact = BasePlayer.Find(nameOrId);
            if (exact != null) return exact;

            return BasePlayer.activePlayerList.FirstOrDefault(p =>
                p != null && p.displayName.Contains(nameOrId, StringComparison.OrdinalIgnoreCase));
        }

        #endregion
    }
}
