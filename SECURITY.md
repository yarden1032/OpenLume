# Security policy

OpenLume is pre-1.0 and receives security fixes on the latest `main` branch.

Do not open a public issue for vulnerabilities involving file overwrite, path traversal, credential disclosure, unsafe native decoding, or arbitrary model/provider responses. Use GitHub's private vulnerability reporting for the repository.

OpenLume's security invariants are:

- Originals are read-only inputs and are never overwrite targets.
- Export uses a new temporary file followed by an atomic move.
- Provider responses are untrusted and must be validated against bounded schemas.
- Secrets never enter the catalog, source tree, logs, or crash reports.
- Model downloads require an allowlisted source, declared license, size limit, and SHA-256 verification.

