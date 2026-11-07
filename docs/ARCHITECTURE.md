# Архитектура DjShrek-Draiver

## Принципы
1. Разделение UI, предметной логики, инфраструктуры и Windows API.
2. Каждый системный модуль имеет интерфейс и тестируемую реализацию.
3. Любая операция, меняющая систему, проходит через явный сценарий подтверждения.
4. Каталог драйверов хранит метаданные и проверяемое происхождение пакетов, а не произвольные исполняемые файлы.
5. Ошибки и отмены отражаются в журнале операций.

## Предлагаемая структура solution

```text
src/
  DjShrek.Draiver.App/             # WPF application, navigation, composition root
  DjShrek.Draiver.Domain/          # entities, value objects, contracts
  DjShrek.Draiver.Application/     # use cases, validation, orchestration
  DjShrek.Draiver.Infrastructure/  # SQLite, logging, repositories
  DjShrek.Draiver.Windows/         # SetupAPI, PnP, signature inspection
  DjShrek.Draiver.Modules/
    Drivers/                       # scan, match, stage, install
    Backup/                        # export and restore workflows
    Monitoring/                    # hardware telemetry adapters
    Diagnostics/                   # diagnostic checks
tests/
  DjShrek.Draiver.Domain.Tests/
  DjShrek.Draiver.Application.Tests/
  DjShrek.Draiver.Windows.Tests/
docs/
.github/workflows/
```

## Основные контракты
- `IDeviceInventory`: получение PnP-устройств и Hardware ID.
- `IDriverSource`: собственный каталог DjShrek-Draiver; без WUAPI и Microsoft Update Catalog.
- `DriverCatalogService`: Hardware ID matching, HTTPS download, SHA-256, INF и подпись издателя.
- `IDriverPackageVerifier`: проверка подписи, издателя, хэша и целевой платформы.
- `IDriverInstaller`: установка только после подтверждения пользователя.
- `IDriverBackupService`: экспорт и проверка резервных копий.
- `IHardwareTelemetryProvider`: чтение датчиков с явным указанием доступности/ошибки.
- `IAuditLog`: журнал действий и результатов.

## Безопасный сценарий обновления
1. Инвентаризация PnP-устройства.
2. Получение Hardware ID, текущей версии и сведений об ОС.
3. Поиск кандидатов в разрешённых источниках.
4. Сопоставление устройства, архитектуры, версии ОС и подписи.
5. Показ пользователю текущей и предлагаемой версии, источника и предупреждений.
6. Создание backup/restore point, когда возможно.
7. Явное подтверждение установки.
8. Установка через поддерживаемые механизмы Windows.
9. Повторная инвентаризация и отчёт; при сбое предложить восстановление.

## Нефункциональные требования
- Не требовать административных прав для просмотра сведений, где это возможно.
- Запрашивать elevation только на конкретную привилегированную операцию.
- Обрабатывать недоступные датчики и устройства без падения приложения.
- Не загружать и не исполнять драйверы из непроверенных URL.
