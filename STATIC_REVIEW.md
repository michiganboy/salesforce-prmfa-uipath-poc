# Superseded static review

The earlier static-only assessment has been superseded by the executed build,
WebSocket regression tests, code fixes and protocol review documented in
[docs/engineering-review.md](docs/engineering-review.md).

In particular, the earlier claim that an existing UiPath CDP SessionId could be
reused on a newly opened WebSocket was incorrect. Use the exact page TargetId
and attach on this library's connection. The earlier source-only review did not
prove Salesforce acceptance or UiPath integration support.
