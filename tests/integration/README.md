# Integration tests

Compose-based tests (dashboard + fake vLLM, and later the supervisor) --
*Integration* level per the Build Plan's test-level convention: real
supervisor + dashboard against the fake vLLM, no GPU. Populated starting
with the phases that need it (P01c onward); empty placeholder from P00.
