# История обновлений — RSleepingSnake

[Описание](./RSleepingSnake.md) | **История обновлений**

# RSleepingSnake v1.0.1 — Hotfix: Carbon & Oxide Config Initialization

## 📌 Описание / Summary
Критическое обновление **v1.0.1** для сервера Rust под управлением **Carbon** (и Oxide). Исправлен сбой жизненного цикла плагина (`NullReferenceException` в `ILoadConfig` и `Init`), возникавший из-за отсутствия вызова базовой инициализации конфигурации.

---

## 🛠 Что исправлено / Changelog

- **[Fix] Carbon/Oxide Config Lifecycle**: Восстановлен обязательный вызов `base.LoadConfig()`, предотвращающий обращение к `null`-объекту `Config` в хуке `ILoadConfig`.
- **[Fix] Init Hook Crash**: Устранен избыточный вызов `LoadConfig()` внутри метода `Init()`.
- **[Fix] Safe Serialization**: В метод `SaveConfig()` добавлена защитная проверка на `null` перед сохранением.
- **[Refactor] Code Standard**: Убран модификатор `partial` класса плагина в соответствии с монолитным регламентом разработки.

---

## ⚙️ Установка на Carbon / Installation for Carbon

1. Скопируйте файл `RSleepingSnake.cs` в папку `carbon/plugins/`.
2. Carbon автоматически скомпилирует плагин.
3. Конфигурационный файл будет сгенерирован по пути: `carbon/configs/RSleepingSnake.json`.

---

## 📊 Инварианты и сложность (Proof Obligations)
- **Zero Allocations in Runtime**: Накладные расходы памяти $O(\text{mem}) = O(1)$.
- **Deterministic Fallback**: При повреждении JSON плагин автоматически восстанавливает дефолтную структуру без остановки сервера.

