# MCP reentrant stop/restart repair (PR-MCP-001)

Status: complete

Scope: McpHttpServer.Stop listener-before-cancellation ordering; CoreAiMcpServer.StopListening captured-resource cleanup; real HTTP regression in McpHttpServerIntegrationEditModeTests.

Fix: release the HTTP listener prefix before invoking cancellation callbacks so a same-port replacement can bind. Captured old transport only; replacement fields are not clobbered. Old CTS disposed after Stop returns.