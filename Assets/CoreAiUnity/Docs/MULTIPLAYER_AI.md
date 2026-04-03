# CoreAI и мультиплеер

## Политика выполнения ИИ

В `CoreAILifetimeScope` (секция **Network / AI authority**):

| Поле | Смысл |
|------|--------|
| **AllPeers** (по умолчанию) | LLM/оркестратор могут выполняться на каждом узле. Удобно для прототипов; возможны дублирующие вызовы и расход токенов. |
| **HostOnly** | ИИ только на узле с `IsHostAuthority` (хост, dedicated server, solo). |
| **ClientPeersOnly** | ИИ только на «чистых» клиентах (`IsPureClient`), без роли хоста. |

Без сетевого слоя используется `DefaultSoloNetworkPeer`: хост = да, чистый клиент = нет.

## Свой peer (Unity Netcode и др.)

Добавьте компонент, унаследованный от `CoreAiNetworkPeerBehaviour`, и назначьте его в поле **network peer behaviour** на `CoreAILifetimeScope`.

Пример логики (псевдокод NGO):

- `IsHostAuthority` → `IsServer` или `IsHost` (listen server).
- `IsPureClient` → `IsClient && !IsHost` (удалённый клиент без host authority).

## Команды и состояние

`ApplyAiGameCommand` и Lua/версии данных по умолчанию локальны. Для мультиплеера обычно нужно:

- Реплицировать **решения**, которые меняют игру (спавн, прогрессия), с сервера.
- Держать **один** источник правды для LLM-трассировки или явно помечать «локальный чат» vs «серверный сценарий».

Подробнее см. комментарии в `ArenaSurvivalProceduralSetup` (роль симуляции).
