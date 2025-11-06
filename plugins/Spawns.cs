//v2.0.38  Добавлена авто установка точек спавна при установке Tesla Coil
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using UnityEngine;

namespace Oxide.Plugins
{
    [
        Info("Spawns", "Reneb / k1lly0u / Robin Play", "2.0.38"),
        Description(
            "A database of sets of spawn points, created by a user and used by other plugins"
        )
    ]
    class Spawns : RustPlugin
    {
        #region Fields
        private SpawnsData _spawnsData;

        private Dictionary<string, List<Vector3>> _loadedSpawnfiles =
            new Dictionary<string, List<Vector3>>();

        private Dictionary<ulong, List<Vector3>> _spawnFileCreators =
            new Dictionary<ulong, List<Vector3>>();

        private List<ulong> _isEditing = new List<ulong>();
        #endregion

        #region Oxide Hooks
        private void Loaded() => LoadData();

        private void OnServerInitialized() => VerifyFilesExist();

        private void OnNewSave(string filename)
        {
            ClearData();
        }

        // CHANGE: Добавлен хук для автоматического добавления точек спавна при установке Tesla Coil
        // WHY: Упрощение процесса установки точек спавна - вместо команды /spawns add достаточно установить Tesla Coil
        // REF: Запрос пользователя об автоматизации установки точек спавна
        private void OnItemDeployed(
            Deployer deployer,
            ItemModDeployable itemModDeployable,
            BaseEntity deployedEntity
        )
        {
            BasePlayer player = deployer?.GetOwnerPlayer();
            if (player == null || !player.IsConnected)
                return;

            // Проверяем, находится ли игрок в режиме создания точек спавна
            if (!IsCreatingFile(player))
            {
                // CHANGE: Добавлен явный вывод причины, почему точка не добавлена
                // WHY: Пользователь не видит фидбек, когда не в режиме создания
                // QUOTE(TЗ): "ставлю катушки... в итоге ничего не просиходит"
                // REF: Запрос пользователя
                Print(player, Lang("notCreating", player.UserIDString));
                return;
            }

            // Проверяем, что установлен именно Tesla Coil по prefab-имени сущности
            // CHANGE: Заменили проверку shortname предмета на проверку ShortPrefabName сущности
            // WHY: Реальный shortname Tesla Coil — "electric.teslacoil"; старая проверка "teslacoil" не срабатывала
            // SOURCE: stringPool.json содержит "teslacoil.deployed.prefab"
            string shortPrefab = deployedEntity?.ShortPrefabName ?? string.Empty;
            string fullPrefab = deployedEntity?.PrefabName ?? string.Empty;
            string itemShort = deployer?.GetItem()?.info?.shortname ?? string.Empty;
            bool isTesla =
                (
                    !string.IsNullOrEmpty(shortPrefab)
                    && shortPrefab.IndexOf("teslacoil", StringComparison.OrdinalIgnoreCase) >= 0
                )
                || (
                    !string.IsNullOrEmpty(fullPrefab)
                    && fullPrefab.IndexOf("teslacoil", StringComparison.OrdinalIgnoreCase) >= 0
                )
                || (
                    !string.IsNullOrEmpty(itemShort)
                    && itemShort.IndexOf("teslacoil", StringComparison.OrdinalIgnoreCase) >= 0
                );
            if (!isTesla)
            {
                return;
            }

            // Автоматически добавляем точку спавна на позиции установленного объекта
            Vector3 spawnPosition = deployedEntity.transform.position;
            _spawnFileCreators[player.userID].Add(spawnPosition);
            int number = _spawnFileCreators[player.userID].Count;

            // Визуализируем добавленную точку спавна
            DDrawPosition(player, spawnPosition, number.ToString());

            // Показываем сообщение об успешном добавлении
            Print(player, Lang("addSpawn", player.UserIDString, number));

            // Удаляем установленный объект Tesla Coil (он не нужен в мире)
            // CHANGE: Исправлен вызов Invoke с указанием задержки
            // WHY: BaseEntity.Invoke требует два параметра: Action и float delay
            deployedEntity.Invoke(
                () =>
                {
                    if (deployedEntity != null && !deployedEntity.IsDestroyed)
                        deployedEntity.Kill(BaseNetworkable.DestroyMode.Gib);
                },
                0.1f
            );
        }

        // CHANGE: Добавили обработчик второй сигнатуры хука Oxide для установки предмета (Slot)
        // WHY: У части билдов Rust вызывается перегрузка с двумя BaseEntity, из-за чего логика не срабатывала
        // SOURCE: hooks.json → "OnItemDeployed(Deployer deployer, BaseEntity baseEntity, BaseEntity baseEntity2)"
        private void OnItemDeployed(Deployer deployer, BaseEntity entity, BaseEntity target)
        {
            BasePlayer player = deployer?.GetOwnerPlayer();
            if (player == null || !player.IsConnected)
                return;

            if (!IsCreatingFile(player))
            {
                Print(player, Lang("notCreating", player.UserIDString));
                return;
            }

            BaseEntity placed = entity ?? target;
            if (placed == null)
                return;

            string shortPrefab = placed.ShortPrefabName ?? string.Empty;
            string fullPrefab = placed.PrefabName ?? string.Empty;
            bool isTesla =
                (
                    !string.IsNullOrEmpty(shortPrefab)
                    && shortPrefab.IndexOf("teslacoil", StringComparison.OrdinalIgnoreCase) >= 0
                )
                || (
                    !string.IsNullOrEmpty(fullPrefab)
                    && fullPrefab.IndexOf("teslacoil", StringComparison.OrdinalIgnoreCase) >= 0
                );
            if (!isTesla)
                return;

            Vector3 spawnPosition = placed.transform.position;
            _spawnFileCreators[player.userID].Add(spawnPosition);
            int number = _spawnFileCreators[player.userID].Count;

            DDrawPosition(player, spawnPosition, number.ToString());
            Print(player, Lang("addSpawn", player.UserIDString, number));

            placed.Invoke(
                () =>
                {
                    if (placed != null && !placed.IsDestroyed)
                        placed.Kill(BaseNetworkable.DestroyMode.Gib);
                },
                0.1f
            );
        }

        // CHANGE: Дополнительный надёжный перехват через OnEntitySpawned по OwnerID
        // WHY: Некоторые билды могут не вызывать OnItemDeployed для отдельных сущностей; по OwnerID можно связать игрока и сущность
        // SOURCE: Префабы teslacoil присутствуют в stringPool.json (teslacoil.deployed.prefab)
        private void OnEntitySpawned(BaseNetworkable networkable)
        {
            BaseEntity entity = networkable as BaseEntity;
            if (entity == null)
                return;

            string shortPrefab = entity.ShortPrefabName ?? string.Empty;
            string fullPrefab = entity.PrefabName ?? string.Empty;
            bool isTesla =
                (
                    !string.IsNullOrEmpty(shortPrefab)
                    && shortPrefab.IndexOf("teslacoil", StringComparison.OrdinalIgnoreCase) >= 0
                )
                || (
                    !string.IsNullOrEmpty(fullPrefab)
                    && fullPrefab.IndexOf("teslacoil", StringComparison.OrdinalIgnoreCase) >= 0
                );
            if (!isTesla)
                return;

            ulong ownerId = entity.OwnerID;
            if (ownerId == 0)
                return;

            if (!_spawnFileCreators.ContainsKey(ownerId))
                return;

            BasePlayer player = BasePlayer.FindByID(ownerId);
            if (player == null || !player.IsConnected)
                return;

            Vector3 spawnPosition = entity.transform.position;
            _spawnFileCreators[ownerId].Add(spawnPosition);
            int number = _spawnFileCreators[ownerId].Count;

            DDrawPosition(player, spawnPosition, number.ToString());
            Print(player, Lang("addSpawn", player.UserIDString, number));

            entity.Invoke(
                () =>
                {
                    if (entity != null && !entity.IsDestroyed)
                        entity.Kill(BaseNetworkable.DestroyMode.Gib);
                },
                0.1f
            );
        }
        #endregion

        #region Functions
        private void VerifyFilesExist()
        {
            bool hasChanged = false;
            for (int i = 0; i < _spawnsData.Spawnfiles.Count; i++)
            {
                string name = _spawnsData.Spawnfiles[i];

                if (!Interface.Oxide.DataFileSystem.ExistsDatafile($"SpawnsDatabase/{name}"))
                {
                    _spawnsData.Spawnfiles.Remove(name);
                    hasChanged = true;
                }
                else
                {
                    if (LoadSpawns(name) != null)
                    {
                        _spawnsData.Spawnfiles.Remove(name);
                        hasChanged = true;
                    }
                    else if (_loadedSpawnfiles[name].Count == 0)
                    {
                        _spawnsData.Spawnfiles.Remove(name);
                        hasChanged = true;
                    }
                }
            }

            if (hasChanged)
                SaveData();
        }

        private object LoadSpawns(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Lang("noFile");

            if (!_loadedSpawnfiles.ContainsKey(name))
            {
                object success = LoadSpawnFile(name);
                if (success == null)
                    return Lang("noFile");
                else
                    _loadedSpawnfiles.Add(name, (List<Vector3>)success);
            }
            return null;
        }
        #endregion

        #region API
        private object GetSpawnsCount(string filename)
        {
            object success = LoadSpawns(filename);
            if (success != null)
                return (string)success;

            return _loadedSpawnfiles[filename].Count;
        }

        private object GetRandomSpawn(string filename)
        {
            object success = LoadSpawns(filename);
            if (success != null)
                return (string)success;

            return _loadedSpawnfiles[filename].GetRandom();
        }

        private object GetRandomSpawnRange(string filename, int min, int max)
        {
            object success = LoadSpawns(filename);
            if (success != null)
                return (string)success;

            List<Vector3> list = _loadedSpawnfiles[filename];

            return list[
                UnityEngine.Random.Range(
                    Mathf.Clamp(min, 0, list.Count - 1),
                    Mathf.Clamp(max, 0, list.Count - 1)
                )
            ];
        }

        private object GetSpawn(string filename, int number)
        {
            object success = LoadSpawns(filename);
            if (success != null)
                return (string)success;

            List<Vector3> list = _loadedSpawnfiles[filename];

            return list[Mathf.Clamp(number, 0, list.Count - 1)];
        }

        private string[] GetSpawnfileNames() => _spawnsData.Spawnfiles.ToArray();
        #endregion

        #region Chat Commands
        [ChatCommand("spawns")]
        void cmdSpawns(BasePlayer player, string command, string[] args)
        {
            if (player.net.connection.authLevel < 1)
            {
                Print(player, Lang("noAccess", player.UserIDString));
                return;
            }

            if (args == null || args.Length == 0)
            {
                SendHelpText(player);
                return;
            }

            if (args.Length >= 1)
            {
                switch (args[0].ToLower())
                {
                    case "new":
                        if (IsCreatingFile(player))
                        {
                            Print(player, Lang("alreadyCreating", player.UserIDString));
                            return;
                        }

                        _spawnFileCreators.Add(player.userID, new List<Vector3>());

                        Print(player, Lang("newCreating", player.UserIDString));
                        return;

                    case "open":
                        if (args.Length >= 2)
                        {
                            if (IsCreatingFile(player))
                            {
                                Print(player, Lang("isCreating", player.UserIDString));
                                return;
                            }
                            object spawns = LoadSpawnFile(args[1]);
                            if (spawns != null)
                            {
                                _spawnFileCreators.Add(player.userID, (List<Vector3>)spawns);
                                Print(
                                    player,
                                    Lang(
                                        "opened",
                                        player.UserIDString,
                                        _spawnFileCreators[player.userID].Count
                                    )
                                );
                                _isEditing.Add(player.userID);
                            }
                            else
                                Print(player, Lang("invalidFile", player.UserIDString));
                        }
                        else
                            Print(player, Lang("fileName", player.UserIDString));
                        return;

                    case "add":
                        if (!IsCreatingFile(player))
                        {
                            Print(player, Lang("notCreating", player.UserIDString));
                            return;
                        }
                        else
                        {
                            _spawnFileCreators[player.userID].Add(player.transform.position);
                            int number = _spawnFileCreators[player.userID].Count;
                            DDrawPosition(
                                player,
                                _spawnFileCreators[player.userID][number - 1],
                                number.ToString()
                            );
                            Print(
                                player,
                                Lang(
                                    "addSpawn",
                                    player.UserIDString,
                                    _spawnFileCreators[player.userID].Count
                                )
                            );
                        }
                        return;

                    case "remove":
                        if (args.Length >= 2)
                        {
                            if (!IsCreatingFile(player))
                            {
                                Print(player, Lang("notCreating", player.UserIDString));
                                return;
                            }

                            if (_spawnFileCreators[player.userID].Count > 0)
                            {
                                int number;
                                if (int.TryParse(args[1], out number))
                                {
                                    if (number <= _spawnFileCreators[player.userID].Count)
                                    {
                                        _spawnFileCreators[player.userID].RemoveAt(number - 1);
                                        Print(
                                            player,
                                            Lang("remSuccess", player.UserIDString, number)
                                        );
                                    }
                                    else
                                        Print(player, Lang("nexistNum", player.UserIDString));
                                }
                                else
                                    Print(player, Lang("noNum", player.UserIDString));
                            }
                            else
                                Print(player, Lang("noSpawnpoints", player.UserIDString));
                        }
                        else
                            SendReply(player, "/spawns remove <number>");
                        return;

                    case "save":
                        if (args.Length >= 2)
                        {
                            if (!IsCreatingFile(player))
                            {
                                Print(player, Lang("noCreate", player.UserIDString));
                                return;
                            }
                            if (
                                _spawnFileCreators.ContainsKey(player.userID)
                                && _spawnFileCreators[player.userID].Count > 0
                            )
                            {
                                if (
                                    !_spawnsData.Spawnfiles.Contains(args[1])
                                    && !_loadedSpawnfiles.ContainsKey(args[1])
                                )
                                {
                                    Print(
                                        player,
                                        Lang(
                                            "saved",
                                            player.UserIDString,
                                            _spawnFileCreators[player.userID].Count,
                                            args[1]
                                        )
                                    );
                                    SaveSpawnFile(player, args[1]);
                                    return;
                                }

                                if (_isEditing.Contains(player.userID))
                                {
                                    SaveSpawnFile(player, args[1]);
                                    Print(
                                        player,
                                        Lang("overwriteSuccess", player.UserIDString, args[1])
                                    );
                                    _isEditing.Remove(player.userID);
                                    return;
                                }

                                Print(player, Lang("spawnfileExists", player.UserIDString));
                                return;
                            }
                            else
                                Print(player, Lang("noSpawnpoints", player.UserIDString));
                        }
                        else
                            SendReply(player, "/spawns save <filename>");
                        return;

                    case "close":
                        if (!IsCreatingFile(player))
                        {
                            Print(player, Lang("noCreate", player.UserIDString));
                            return;
                        }
                        _spawnFileCreators.Remove(player.userID);
                        Print(player, Lang("noSave", player.UserIDString));
                        return;

                    case "show":
                        if (!IsCreatingFile(player))
                        {
                            Print(player, Lang("notCreating", player.UserIDString));
                            return;
                        }
                        if (_spawnFileCreators[player.userID].Count > 0)
                        {
                            float time = 10f;
                            if (args.Length > 1)
                                float.TryParse(args[1], out time);

                            for (int i = 0; i < _spawnFileCreators[player.userID].Count; i++)
                                DDrawPosition(
                                    player,
                                    _spawnFileCreators[player.userID][i],
                                    i.ToString(),
                                    time
                                );

                            return;
                        }
                        else
                            Print(player, Lang("noSp", player.UserIDString));
                        return;

                    default:
                        SendHelpText(player);
                        break;
                }
            }
        }

        private void DDrawPosition(BasePlayer player, Vector3 point, string name, float time = 10f)
        {
            player.SendConsoleCommand(
                "ddraw.text",
                time,
                Color.green,
                point + new Vector3(0, 1.5f, 0),
                $"<size=40>{name}</size>"
            );
            player.SendConsoleCommand("ddraw.box", time, Color.green, point, 1f);
        }

        private void SendHelpText(BasePlayer player)
        {
            var lines = new string[]
            {
                Lang("newSyn", player.UserIDString),
                Lang("openSyn", player.UserIDString),
                Lang("addSyn", player.UserIDString),
                Lang("remSyn", player.UserIDString),
                Lang("saveSyn", player.UserIDString),
                Lang("closeSyn", player.UserIDString),
                Lang("showSyn", player.UserIDString),
            };

            // CHANGE: Добавлен префикс плагина Spawns первой строкой
            // WHY: Пользователь просил выводить префикс сверху
            // QUOTE(TЗ): "сделай префикс плагина Spawns ... сначала должен быть префикс плагина, а ниже идти список команд"
            var header = Lang("header", player.UserIDString);
            Print(player, header + "\n" + string.Join("\n", lines));
        }

        private bool IsCreatingFile(BasePlayer player) =>
            _spawnFileCreators.ContainsKey(player.userID);
        #endregion

        #region Data Management
        private DynamicConfigFile data;

        private void SaveData() => data.WriteObject(_spawnsData);

        private void LoadData()
        {
            data = Interface.Oxide.DataFileSystem.GetFile("SpawnsDatabase/spawns_data");

            try
            {
                _spawnsData = data.ReadObject<SpawnsData>();
            }
            catch
            {
                _spawnsData = new SpawnsData();
            }
        }

        private void ClearData()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(
                    "SpawnsDatabase/spawns_data",
                    new SpawnsData()
                );
                Puts("Обнаружена новая карта (или вайп) - данные успешно удалены");
                _spawnsData.Spawnfiles.Clear();
                _loadedSpawnfiles.Clear();
                _spawnFileCreators.Clear();
                _isEditing.Clear();
            }
            catch (Exception ex)
            {
                PrintError($"Ошибка при очистке данных: {ex.Message}");
            }
        }

        private void SaveSpawnFile(BasePlayer player, string name)
        {
            DynamicConfigFile configFile = Interface.Oxide.DataFileSystem.GetFile(
                $"SpawnsDatabase/{name}"
            );
            configFile.Clear();
            configFile.Settings.Converters = new JsonConverter[]
            {
                new StringEnumConverter(),
                new UnityVector3Converter(),
            };

            Spawnfile spawnFile = new Spawnfile();

            for (int i1 = 0; i1 < _spawnFileCreators[player.userID].Count; i1++)
            {
                Vector3 spawnpoint = _spawnFileCreators[player.userID][i1];

                spawnFile.spawnPoints.Add(i1.ToString(), spawnpoint);
            }

            configFile.WriteObject(spawnFile);

            if (!_spawnsData.Spawnfiles.Contains(name))
                _spawnsData.Spawnfiles.Add(name);

            if (!_loadedSpawnfiles.ContainsKey(name))
                _loadedSpawnfiles.Add(name, _spawnFileCreators[player.userID]);
            else
                _loadedSpawnfiles[name] = _spawnFileCreators[player.userID];

            SaveData();

            _spawnFileCreators.Remove(player.userID);
        }

        private object LoadSpawnFile(string name)
        {
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile($"SpawnsDatabase/{name}"))
                return null;

            DynamicConfigFile configFile = Interface
                .GetMod()
                .DataFileSystem.GetDatafile($"SpawnsDatabase/{name}");
            configFile.Settings.Converters = new JsonConverter[]
            {
                new StringEnumConverter(),
                new UnityVector3Converter(),
            };

            Spawnfile spawnFile = new Spawnfile();
            spawnFile = configFile.ReadObject<Spawnfile>();

            List<Vector3> list = spawnFile.spawnPoints.Values.ToList();
            if (list.Count < 1)
                return null;

            return list;
        }

        private class SpawnsData
        {
            public List<string> Spawnfiles = new List<string>();
        }

        private class Spawnfile
        {
            public Dictionary<string, Vector3> spawnPoints = new Dictionary<string, Vector3>();
        }

        private class UnityVector3Converter : JsonConverter
        {
            public override void WriteJson(
                JsonWriter writer,
                object value,
                JsonSerializer serializer
            )
            {
                Vector3 vector = (Vector3)value;
                writer.WriteValue($"{vector.x} {vector.y} {vector.z}");
            }

            public override object ReadJson(
                JsonReader reader,
                Type objectType,
                object existingValue,
                JsonSerializer serializer
            )
            {
                if (reader.TokenType == JsonToken.String)
                {
                    string[] values = reader.Value.ToString().Trim().Split(' ');
                    return new Vector3(
                        Convert.ToSingle(values[0]),
                        Convert.ToSingle(values[1]),
                        Convert.ToSingle(values[2])
                    );
                }
                JObject o = JObject.Load(reader);
                return new Vector3(
                    Convert.ToSingle(o["x"]),
                    Convert.ToSingle(o["y"]),
                    Convert.ToSingle(o["z"])
                );
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Vector3);
            }
        }
        #endregion

        #region LanguageFile

        // Выводит готовое локализованное сообщение игроку
        private void Print(BasePlayer player, string message)
        {
            Player.Message(player, message, string.Empty, 0ul);
        }

        // Возвращает строку-шаблон из lang-файла и подставляет аргументы
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

        // Регистрирует наборы сообщений (en, ru и т. д.)
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["header"] = "<color=#ffc859>Spawns</color>",
                    ["noFile"] = "This file doesn't exist",
                    ["alreadyCreating"] = "You are already creating a spawn file",
                    ["newCreating"] =
                        "You now creating a new spawn file. Place Tesla Coil to add spawn points automatically or use /spawns add",
                    ["isCreating"] =
                        "You must save/close your current spawn file first. Type /spawns for more information",
                    ["opened"] = "Opened spawnfile with {0} spawns",
                    ["invalidFile"] = "This spawnfile is empty or not valid",
                    ["fileName"] = "You must enter a filename",
                    ["notCreating"] =
                        "You must create/open a new Spawn file first /spawns for more information",
                    ["remSuccess"] = "Successfully removed spawn n°{0}",
                    ["nexistNum"] = "This spawn number doesn't exist",
                    ["noNum"] = "You must enter a spawn point number",
                    ["noSpawnpoints"] = "You haven't set any spawn points yet",
                    ["noCreate"] =
                        "You must create a new Spawn file first. Type /spawns for more information",
                    ["noSave"] = "Spawn file closed without saving",
                    ["noSp"] = "You must add spawnpoints first",
                    ["newSyn"] = "<color=#ffc859>/spawns new</color> - Create a new spawn file",
                    ["openSyn"] =
                        "<color=#ffc859>/spawns open</color> - Open a existing spawn file for editing",
                    ["addSyn"] = "<color=#ffc859>/spawns add</color> - Add a new spawn point",
                    ["remSyn"] =
                        "<color=#ffc859>/spawns remove <number></color> - Remove a spawn point",
                    ["saveSyn"] =
                        "<color=#ffc859>/spawns save <filename></color> - Saves your spawn file",
                    ["closeSyn"] =
                        "<color=#ffc859>/spawns close</color> - Cancel spawn file creation",
                    ["showSyn"] =
                        "<color=#ffc859>/spawns show <opt:time></color> - Display a box at each spawnpoint",
                    ["noAccess"] = "You are not allowed to use this command",
                    ["saved"] = "{0} spawnpoints saved into {1}",
                    ["spawnfileExists"] = "A spawn file with that name already exists",
                    ["overwriteSuccess"] = "You have successfully edited the spawnfile {0}",
                    ["addSpawn"] = "Added Spawn n°{0}",
                },
                this
            );

            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["header"] = "<color=#ffc859>Spawns</color>",
                    ["noFile"] = "Этот файл не существует",
                    ["alreadyCreating"] = "Вы уже создаете файл спавнов",
                    ["newCreating"] =
                        "Теперь вы создаете новый файл спавнов. Установите Tesla Coil для автоматического добавления точек спавна или используйте /spawns add",
                    ["isCreating"] =
                        "Сначала сохраните/закройте текущий файл спавнов. Введите /spawns для получения информации",
                    ["opened"] = "Открыт файл спавнов с {0} спавнами",
                    ["invalidFile"] = "Этот файл спавнов пуст или недействителен",
                    ["fileName"] = "Вы должны ввести имя файла",
                    ["notCreating"] =
                        "Сначала создайте/откройте новый файл спавнов /spawns для получения информации",
                    ["remSuccess"] = "Успешно удален спавн №{0}",
                    ["nexistNum"] = "Этот номер спавна не существует",
                    ["noNum"] = "Вы должны ввести номер точки спавна",
                    ["noSpawnpoints"] = "Вы еще не установили ни одной точки спавна",
                    ["noCreate"] =
                        "Сначала создайте новый файл спавнов. Введите /spawns для получения информации",
                    ["noSave"] = "Файл спавнов закрыт без сохранения",
                    ["noSp"] = "Сначала добавьте точки спавна",
                    ["newSyn"] = "<color=#ffc859>/spawns new</color> - Создать новый файл спавнов",
                    ["openSyn"] =
                        "<color=#ffc859>/spawns open</color> - Открыть существующий файл спавнов для редактирования",
                    ["addSyn"] = "<color=#ffc859>/spawns add</color> - Добавить новую точку спавна",
                    ["remSyn"] =
                        "<color=#ffc859>/spawns remove <номер></color> - Удалить точку спавна",
                    ["saveSyn"] =
                        "<color=#ffc859>/spawns save <имя_файла></color> - Сохранить ваш файл спавнов",
                    ["closeSyn"] =
                        "<color=#ffc859>/spawns close</color> - Отменить создание файла спавнов",
                    ["showSyn"] =
                        "<color=#ffc859>/spawns show <опц:время></color> - Показать коробку на каждой точке спавна",
                    ["noAccess"] = "Вам не разрешено использовать эту команду",
                    ["saved"] = "{0} точек спавна сохранено в {1}",
                    ["spawnfileExists"] = "Файл спавнов с таким именем уже существует",
                    ["overwriteSuccess"] = "Вы успешно отредактировали файл спавнов {0}",
                    ["addSpawn"] = "Добавлен спавн №{0}",
                },
                this,
                "ru"
            );
        }

        #endregion
    }
}
