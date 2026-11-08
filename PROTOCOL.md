# Catalogue helper boundary

The executable accepts one UTF-8 JSON request on redirected stdin, processes it locally, emits one bounded JSON response on stdout, and exits. Progress is separate bounded JSON on stderr. There is no listening socket, shared-memory interface, caller callback, frontend assembly reference, or game connection.

Requests describe local input files and an operation: index, search, travelSearch, travelDestinations, travelDoor, detail, sourceRecord, placedReference, crossref, status, assets, thumbnail, modelVariants or model. The response contains ordinary plugin-record data, scalar identities, file provenance, images or mesh arrays. C# library object instances and runtime/game handles never cross this interface. The helper owns its private derived index and atomic rebuild lifecycle. Callers own their interfaces and game/session behavior independently.

The original helper verifies a signed customer authorization supplied as request data. The public verification resource is included to reproduce that original binary; it contains no signing key, service token or customer credential. Covered-code modification rights remain governed by the GPL.

Neither this document nor a process boundary alone establishes a legal conclusion about every possible integration. This release supplies the helper and its linked GPL dependencies under the GPL, with corresponding source, and excludes the separately implemented desktop/game/commerce programs from this source repository.
