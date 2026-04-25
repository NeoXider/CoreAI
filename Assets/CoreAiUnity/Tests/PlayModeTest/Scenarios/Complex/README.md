# Complex PlayMode Scenarios

This folder is for long-running, behavior-heavy, production-like PlayMode scenarios.

Examples:
- multi-step crafting with memory + tools
- chat negotiation with merchant tools and economy state
- deterministic replay checks on repeated inputs

Keep these scenarios separate from fast smoke/integration PlayMode tests so they can be run as an isolated suite.
