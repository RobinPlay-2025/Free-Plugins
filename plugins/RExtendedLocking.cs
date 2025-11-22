using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RExtendedLocking", "Robin Play", "1.0.10")]
    [Description("Установка замков на бочки из DLS, компостер, улей и деревянные ставни")]
    public class RExtendedLocking : RustPlugin
    {
        #region Prefabs
        /// <summary>
        /// Использование HashSet для быстрой проверки вхождения
        /// </summary>
        private readonly HashSet<uint> SupportedBarrelsIDs = new HashSet<uint>();
        private uint _composterID;
        private uint _beehiveID;

        /// <summary>
        /// Полные пути префабов
        /// </summary>
        private readonly string[] BarrelPrefabs =
        {
            "assets/prefabs/misc/decor_dlc/storagebarrel/storage_barrel_b.prefab",
            "assets/prefabs/misc/decor_dlc/storagebarrel/storage_barrel_c.prefab",
            "assets/prefabs/deployable/large wood storage/skins/jungle_dlc_large_wood_box/jungle_dlc_storage_vertical/bamboo_barrel.prefab",
            "assets/prefabs/deployable/large wood storage/skins/jungle_dlc_large_wood_box/jungle_dlc_storage_horizontal/wicker_barrel.prefab",
            "assets/prefabs/deployable/large wood storage/skins/abyss_dlc_large_wood_box/abyss_dlc_storage_horizontal/abyss_barrel_horizontal.prefab",
            "assets/prefabs/deployable/large wood storage/skins/abyss_dlc_large_wood_box/abyss_dlc_storage_vertical/abyss_barrel_vertical.prefab",
            "assets/prefabs/deployable/large wood storage/skins/warhammer_dlc_large_wood_box/krieg_storage_horizontal/krieg_storage_horizontal.prefab",
            "assets/prefabs/deployable/large wood storage/skins/warhammer_dlc_large_wood_box/krieg_storage_vertical/krieg_storage_vertical.prefab",
        };

        private const string ComposterPrefab =
            "assets/prefabs/deployable/composter/composter.prefab";

        private const string BeehivePrefab =
            "assets/prefabs/deployable/beehive/beehive.deployed.prefab";
        #endregion Prefabs

        /// <summary>
        /// Вызывается, когда сервер полностью инициализирован
        /// </summary>
        /// <param name="serverInitialized">Признак того, что сервер полностью инициализирован.</param>
        [UsedImplicitly]
        private void OnServerInitialized(bool serverInitialized = false)
        {
            // Инициализация префабов
            InitPrefabs();

            // Делаем существующие объекты блокируемыми
            ProcessExistingEntities();
        }

        /// <summary>
        /// Инициализация префабов — запускаем один раз при старте сервера
        /// </summary>
        private void InitPrefabs()
        {
            // Очищаем коллекцию на случай перезагрузки плагина
            SupportedBarrelsIDs.Clear();

            // Получаем ID компостера
            _composterID = StringPool.Get(ComposterPrefab);

            // Получаем ID улья
            _beehiveID = StringPool.Get(BeehivePrefab);

            // Получаем ID для всех бочек
            foreach (var barrelPrefab in BarrelPrefabs)
            {
                uint id = StringPool.Get(barrelPrefab);
                if (id > 0)
                    SupportedBarrelsIDs.Add(id);
            }
        }

        /// <summary>
        /// Вызывается при спауне сущности в игре
        /// </summary>
        /// <param name="entity">Сущность, которая была заспавнена.</param>
        [UsedImplicitly]
        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity == null)
                return;

            uint prefabID = entity.prefabID;

            // Бочки
            if (
                entity is BoxStorage box
                && SupportedBarrelsIDs.Contains(prefabID)
                && !box.isLockable
            )
            {
                box.isLockable = true;
                box.SendNetworkUpdate();
                return;
            }

            // Компостер
            if (entity is Composter composter && prefabID == _composterID && !composter.isLockable)
            {
                composter.isLockable = true;
                composter.SendNetworkUpdate();
                return;
            }

            // Улей
            if (entity is Beehive beehive && prefabID == _beehiveID && !beehive.isLockable)
            {
                beehive.isLockable = true;
                beehive.SendNetworkUpdate();
                return;
            }

            // Деревянные ставни
            if (
                entity is Door door
                && door.ShortPrefabName == "shutter.wood.a"
                && !door.canTakeLock
            )
            {
                door.canTakeLock = true;
                door.SendNetworkUpdate();
                return;
            }

            // Замок
            if (entity is BaseLock baseLock)
            {
                var parent = baseLock.GetParentEntity();

                // Для компостера
                if (parent is Composter parentComposter && parentComposter.prefabID == _composterID)
                {
                    baseLock.transform.localPosition = new Vector3(0f, 1.3f, 0.6f);
                    baseLock.transform.localRotation = Quaternion.Euler(0, 90, 0);
                    baseLock.SendNetworkUpdate();
                    return;
                }

                // Для улья
                if (parent is Beehive parentBeehive && parentBeehive.prefabID == _beehiveID)
                {
                    baseLock.transform.localPosition = new Vector3(0f, 0.6f, 0.2f);
                    baseLock.transform.localRotation = Quaternion.Euler(0, 90, 0);
                    baseLock.SendNetworkUpdate();
                    return;
                }

                // Для деревянных ставней
                if (parent is Door parentDoor && parentDoor.ShortPrefabName == "shutter.wood.a")
                {
                    baseLock.transform.localPosition = new Vector3(-0.1f, -0.2f, 0.0f);
                    baseLock.transform.localRotation = Quaternion.Euler(0, 180, 0);
                    baseLock.SendNetworkUpdate();
                }
            }
        }

        /// <summary>
        /// Обработка уже существующих объектов на сервере
        /// </summary>
        private void ProcessExistingEntities()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                uint prefabID = entity.prefabID;

                // Бочки
                if (
                    entity is BoxStorage box
                    && SupportedBarrelsIDs.Contains(prefabID)
                    && !box.isLockable
                )
                {
                    box.isLockable = true;
                    box.SendNetworkUpdate();
                    continue;
                }

                // Компостеры
                if (
                    entity is Composter composter
                    && prefabID == _composterID
                    && !composter.isLockable
                )
                {
                    composter.isLockable = true;
                    composter.SendNetworkUpdate();
                    continue;
                }

                // Ульи
                if (entity is Beehive beehive && prefabID == _beehiveID && !beehive.isLockable)
                {
                    beehive.isLockable = true;
                    beehive.SendNetworkUpdate();
                    continue;
                }

                // Деревянные ставни
                if (
                    entity is Door door
                    && door.ShortPrefabName == "shutter.wood.a"
                    && !door.canTakeLock
                )
                {
                    door.canTakeLock = true;
                    door.SendNetworkUpdate();
                }
            }
        }
    }
}
