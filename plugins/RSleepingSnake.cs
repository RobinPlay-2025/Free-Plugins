using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RSleepingSnake", "Robin Play", "1.0.0")]
    [Description("Спавн змеи при сборе ресурсов в умеренных биомах")]
    public partial class RSleepingSnake : RustPlugin
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
            Puts("          Plugin by Robin Play                ");
            Puts("--------------------------------------------------");
            Puts("  VK: vk.com/robinplay2025                    ");
            Puts("  Discord: robin_play                         ");
            Puts("  Telegram: t.me/RobinPlay                    ");
            Puts("==================================================");

            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                {
                    throw new System.Exception("Config is null");
                }
                if (configData.TriggeringPrefabs != null)
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
            configData = new ConfigData();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(configData);
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
