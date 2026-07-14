# Schedule assistant

The deterministic rule-based explanation is always available and never uses a network.

In production server mode, optional AI narration is configured only on the server:

```text
Assistant__Endpoint=https://api.openai.com/v1/
Assistant__Model=gpt-4.1-mini
Assistant__ApiKey=<secret-store value>
```

The browser sends bounded schedule facts to the authenticated application endpoint. The server adds the fixed system instruction, calls the configured HTTPS provider with a timeout and returns narration lines. The key and provider authorization header never enter browser storage or API responses. Provider failures fall back to the rule-based explanation.

Offline demo mode retains optional BYOK configuration for local demonstrations. That key is browser-local and unsuitable for valuable credentials or shared machines.
