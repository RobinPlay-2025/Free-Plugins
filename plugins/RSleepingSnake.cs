using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RSleepingSnake", "RustInnovate", "1.0.1")]
    [Description("Спавн змеи при сборе ресурсов в умеренных биомах")]
    // CHANGE: Убран модификатор partial в соответствии с регламентом монолитных плагинов
    public class RSleepingSnake : RustPlugin
    {
        private ConfigData configData;

        private sealed class ConfigData
        {
            [JsonProperty("Вероятность спавна змеи (0.0 - 1.0)")]
            public float SpawnChance { get; set; } = 0.05f;

            [JsonProperty("Требовать умеренный биом")]
            public bool RequireTemperateBiome { get; set; } = true;

            [JsonProperty("Префабы ресурсов, при сборе которых может появиться змея")]
            public List<string> TriggeringPrefabs { get; set; } =
                new List<string>
                {
                    "wood-collectable",
                    "sulfur-collectable",
                    "metal-collectable",
                    "stone-collectable",
                };
        }

        private void Init()
        {
            Puts($"Загрузка плагина RSleepingSnake v{Version}");
            Puts("==================================================");
            Puts("          Plugin by RustInnovate              ");
            Puts("--------------------------------------------------");
            Puts("  VK: vk.ru/rustinnovate                  ");
            Puts("  Discord: discord.gg/RFm5wruE86                         ");
            Puts("  Telegram: t.me/RobinPlay                    ");
            Puts("==================================================");
            // CHANGE: Удален избыточный вызов LoadConfig(), так как Oxide вызывает LoadConfig до вызова Init()
        }

        // CHANGE: Добавлен обязательный вызов base.LoadConfig() для инициализации свойства Config в Oxide/Carbon
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
                else if (configData.TriggeringPrefabs != null)
                {
                    var uniquePrefabs = new List<string>();
                    foreach (var prefab in configData.TriggeringPrefabs)
                    {
                        if (!uniquePrefabs.Contains(prefab))
                        {
                            uniquePrefabs.Add(prefab);
                        }
                    }
                    configData.TriggeringPrefabs = uniquePrefabs;
                }
            }
            catch
            {
                PrintWarning("Ошибка загрузки конфига. Создаю новый файл.");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            // CHANGE: Инициализация нового экземпляра конфигурации по умолчанию
            configData = new ConfigData();
        }

        protected override void SaveConfig()
        {
            // CHANGE: Защитная проверка Config на null и сохранение с форматированием
            if (Config != null)
            {
                Config.WriteObject(configData, true);
            }
        }

        private void OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player, bool eat)
        {
            if (player == null || collectible == null || configData == null)
                return;

            string prefabName = collectible.ShortPrefabName;
            if (string.IsNullOrEmpty(prefabName))
                return;

            if (
                configData.TriggeringPrefabs == null
                || !configData.TriggeringPrefabs.Contains(prefabName)
            )
                return;

            Vector3 position = collectible.transform.position;
            if (position == Vector3.zero)
            {
                position = player.transform.position;
            }

            if (configData.RequireTemperateBiome)
            {
                int biomeType = TerrainMeta.BiomeMap?.GetBiomeMaxType(position) ?? 0;
                if (biomeType != 2)
                    return;
            }

            float randomValue = UnityEngine.Random.Range(0f, 1f);
            if (randomValue > configData.SpawnChance)
                return;

            SpawnSnake(position);
        }

        private void SpawnSnake(Vector3 position)
        {
            const string snakePrefab = "assets/rust.ai/agents/snake/snake.entity.prefab";

            BaseEntity entity = GameManager.server.CreateEntity(
                snakePrefab,
                position,
                Quaternion.identity
            );
            if (entity == null)
            {
                PrintWarning($"Не удалось создать змею по префабу: {snakePrefab}");
                return;
            }

            entity.enableSaving = false;
            entity.Spawn();
        }
    }
}
