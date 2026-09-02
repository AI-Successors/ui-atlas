# Autotest / AI-agent integration sample

This sample turns a UiAtlas graph into a small, deterministic list of controls an autotest or computer-use agent can consume. It does not click the application: execution remains the responsibility of the test runner or agent, while UiAtlas supplies observed labels, stable selectors, supported actions, and known destination states.

```powershell
dotnet UiAtlas.Core.Consumer.dll C:\maps\hotel.db --query "reservation" --json
```

The JSON response contains:

- the semantic control ID and owning surface;
- stable selectors such as `automationId`, `className`, and `controlType`;
- supported actions (`Invoke`, `Select`, `Toggle`, and others);
- destination state IDs observed during recording;
- the number of evidence observations backing the control.

A safe-export JSON can also be read, but redaction intentionally removes labels and durable application identities. For local automation, use the SQLite map or a trusted full-evidence export and keep it on the same secured machine.
