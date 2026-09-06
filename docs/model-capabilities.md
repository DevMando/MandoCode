# Model image capabilities

Model validation reads Ollama's `/api/show` response. The CLI and Desktop display
the detected status and include it in the agent's system instructions:

- **Vision supported:** the capabilities array contains `vision`.
- **Text-only model:** a valid, nonempty capabilities array omits `vision`.
- **Vision capability unknown:** metadata is missing, empty, or malformed.

Unknown metadata does not fail an otherwise successful model validation. HTTP
and connection failures retain the existing model validation behavior and leave
vision support unknown. No model-name guesses or paid image probes are used.

Detection runs during model validation and when reinitialization or settings
changes select an uninspected endpoint/model. Results belong to that endpoint and
effective model name. Late responses cannot replace a newer inspection or apply
to a different model. Ordinary settings changes preserve conversation history.

`AIService.VisionSupport` exposes the three states. `SupportsImageInput` is true
only for confirmed support. This describes the model, not the host application's
ability to deliver images. Browser tools must also supply actual image content
before enabling visual inspection; returning a screenshot path is insufficient.
Text/DOM browser tools need no vision capability. This change does not add browser
inspection tools, screenshot capture, or image message delivery.

Provider contract: https://docs.ollama.com/api-reference/show-model-details
