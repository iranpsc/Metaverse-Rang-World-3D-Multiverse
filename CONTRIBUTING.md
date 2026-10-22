# Contributing to Metaverse Rang World 3D Multiverse

Thank you for your interest in contributing to **Metaverse Rang World 3D Multiverse**.

This repository contains the core components of the Metaverse Rang ecosystem, including the Unity project, realtime infrastructure, authentication, room management, communication layers, and dedicated game server integration.

We welcome contributions that improve the stability, performance, security, scalability, documentation, and overall quality of the project.

Please read this document before opening an Issue or Pull Request.

---

## Table of Contents

* [Code of Conduct](#code-of-conduct)
* [Ways to Contribute](#ways-to-contribute)
* [Before You Start](#before-you-start)
* [Repository Structure](#repository-structure)
* [Development Workflow](#development-workflow)
* [Branching Strategy](#branching-strategy)
* [Commit Messages](#commit-messages)
* [Issues](#issues)
* [Bug Reports](#bug-reports)
* [Feature Requests](#feature-requests)
* [Pull Requests](#pull-requests)
* [Code Guidelines](#code-guidelines)
* [Unity Contributions](#unity-contributions)
* [Server Contributions](#server-contributions)
* [Realtime Contributions](#realtime-contributions)
* [gRPC and Protobuf Changes](#grpc-and-protobuf-changes)
* [Database Changes](#database-changes)
* [Security](#security)
* [Performance](#performance)
* [Testing](#testing)
* [Documentation](#documentation)
* [Review Process](#review-process)
* [What We Do Not Accept](#what-we-do-not-accept)
* [License](#license)

---

## Code of Conduct

Participation in this project is subject to the repository's **Code of Conduct**.

Please be respectful, constructive, and professional when communicating with maintainers and other contributors.

Harassment, discrimination, personal attacks, deliberate disruption, or other unacceptable behavior will not be tolerated.

Please review:

* `CODE_OF_CONDUCT.md`

---

## Ways to Contribute

There are many ways to contribute beyond writing code.

You can contribute by:

* Reporting reproducible bugs
* Improving documentation
* Improving existing systems
* Fixing performance issues
* Improving realtime stability
* Improving authentication and security
* Improving Unity gameplay systems
* Improving room management
* Improving dedicated server allocation
* Improving WebSocket or gRPC transport
* Adding tests
* Reviewing Pull Requests
* Improving developer tooling
* Improving deployment and infrastructure
* Suggesting well-defined features
* Improving error handling and observability

Small improvements are welcome when they are focused and clearly explained.

---

## Before You Start

Before making a contribution:

1. Make sure you understand the part of the system you want to modify.
2. Search existing Issues and Pull Requests to avoid duplicate work.
3. Read the relevant documentation.
4. Check whether your change affects another subsystem.
5. For large architectural changes, open an Issue or discussion before implementation.
6. Never commit credentials, private keys, tokens, certificates, database credentials, or other secrets.

For security-sensitive issues, do **not** create a public Issue.

Instead, follow the instructions in:

* `SECURITY.md`

---

# Repository Structure

The repository contains both the Unity project and supporting infrastructure.

The Unity project follows the standard Unity layout, including:

```text
Assets/
Packages/
ProjectSettings/
```

The server-side architecture is organized around separated responsibilities such as:

```text
src/
├── config/
├── core/
├── domain/
├── infra/
├── transport/
├── realTime/
├── gameServerControl/
├── integrations/
├── health/
└── http/

protos/
envoy/
```

The server architecture separates configuration, authentication, persistence, transport, realtime communication, room management, and dedicated game-server functionality.

Do not introduce unrelated responsibilities into existing layers merely to simplify a local implementation.

---

# Development Workflow

The recommended workflow is:

```text
Issue / Feature
      ↓
Create Branch
      ↓
Implement
      ↓
Run Tests / Validation
      ↓
Review Changes
      ↓
Commit
      ↓
Push Branch
      ↓
Open Pull Request
      ↓
Code Review
      ↓
Changes / Approval
      ↓
Merge
```

Keep changes focused.

A Pull Request should ideally solve one problem or implement one clearly defined feature.

Avoid combining unrelated changes such as:

* Feature implementation
* Large refactoring
* Formatting changes
* Dependency upgrades
* Unrelated bug fixes

in a single Pull Request.

---

# Branching Strategy

Create a dedicated branch for every change.

Recommended naming conventions:

```text
feature/<short-description>
```

Example:

```text
feature/player-presence
```

For bug fixes:

```text
fix/<short-description>
```

Example:

```text
fix/realtime-disconnect
```

For performance improvements:

```text
perf/<short-description>
```

Example:

```text
perf/room-broadcast
```

For security changes:

```text
security/<short-description>
```

Example:

```text
security/token-validation
```

For documentation:

```text
docs/<short-description>
```

Example:

```text
docs/realtime-protocol
```

For refactoring:

```text
refactor/<short-description>
```

Example:

```text
refactor/realtime-router
```

Avoid generic branch names such as:

```text
test
changes
update
new
fix
branch1
```

---

# Commit Messages

Commit messages should be clear, concise, and descriptive.

Use an imperative style.

Good examples:

```text
feat: add player presence service
```

```text
fix: prevent stale realtime connections
```

```text
perf: reduce room broadcast overhead
```

```text
refactor: separate game server allocation logic
```

```text
docs: improve realtime protocol documentation
```

```text
security: validate game server tickets
```

Recommended prefixes:

| Prefix     | Purpose                                    |
| ---------- | ------------------------------------------ |
| `feat`     | New functionality                          |
| `fix`      | Bug fix                                    |
| `perf`     | Performance improvement                    |
| `refactor` | Code restructuring without behavior change |
| `security` | Security-related change                    |
| `docs`     | Documentation                              |
| `test`     | Tests                                      |
| `build`    | Build/dependency changes                   |
| `ci`       | CI/CD changes                              |
| `chore`    | Maintenance                                |

Keep commits logically separated.

Avoid commits such as:

```text
fix stuff
update
changes
final
final2
new version
```

---

# Issues

Before opening an Issue:

1. Search existing Issues.
2. Check whether the problem exists on the latest relevant branch.
3. Verify that the problem is reproducible.
4. Collect relevant logs and environment information.
5. Remove secrets and sensitive information from logs.

A good Issue should contain:

* Clear title
* Problem description
* Expected behavior
* Actual behavior
* Steps to reproduce
* Environment
* Relevant logs
* Screenshots or videos when useful
* Minimal reproduction when possible

---

# Bug Reports

A useful bug report should answer:

### What happened?

Clearly describe the problem.

### What did you expect?

Describe the expected behavior.

### How can we reproduce it?

Provide deterministic steps.

Example:

```text
1. Start the server.
2. Connect two clients to the same Room.
3. Disconnect client A.
4. Reconnect client A.
5. Observe the Presence state.
```

### Environment

Include relevant information such as:

```text
OS:
Unity version:
Node.js version:
Server version:
Database version:
Browser:
Client platform:
```

Do not include:

```text
JWT tokens
Passwords
Private keys
Database credentials
Internal secrets
```

---

# Feature Requests

Feature requests should describe the problem before proposing the implementation.

A good feature request should explain:

* What problem does this solve?
* Who benefits from it?
* What is the expected behavior?
* Does it affect existing clients?
* Does it require a protocol change?
* Does it affect database schemas?
* Does it affect realtime communication?
* Does it introduce new infrastructure requirements?

For large architectural changes, please open an Issue before starting implementation.

---

# Pull Requests

Every Pull Request should:

* Have a clear title
* Explain what changed
* Explain why it changed
* Reference the relevant Issue when applicable
* Contain only related changes
* Include tests or validation where appropriate
* Update documentation when behavior changes
* Avoid unnecessary generated files
* Avoid unrelated formatting changes
* Avoid committing secrets

A Pull Request description should include:

```markdown
## Summary

Describe the change.

## Motivation

Explain why the change is needed.

## Changes

- Change 1
- Change 2
- Change 3

## Testing

Describe how the change was tested.

## Breaking Changes

Describe any breaking changes.

## Related Issues

Closes #123
```

---

# Pull Request Quality

Before submitting a Pull Request, review your own changes.

Check:

* Is the implementation necessary?
* Is the architecture consistent with the existing system?
* Are responsibilities in the correct layer?
* Could the change break existing clients?
* Could it create a race condition?
* Could it leak sensitive information?
* Does it introduce unnecessary dependencies?
* Are errors handled correctly?
* Are logs useful without exposing secrets?
* Does it negatively affect performance?
* Does documentation need updating?

Do not rely solely on automated checks.

---

# Code Guidelines

The project contains multiple technology layers. Follow the conventions already established in the area you are modifying.

General rules:

### Keep responsibilities separated

Avoid mixing:

* Database access
* Business logic
* Transport logic
* Authentication
* Realtime state
* HTTP handling

inside a single module.

### Prefer small modules

A module should have a clear responsibility.

### Avoid unnecessary abstractions

Do not introduce a framework, dependency, service, or abstraction unless it solves a real problem.

### Handle errors explicitly

Errors should be:

* Detectable
* Meaningful
* Logged appropriately
* Converted to the correct protocol response

### Avoid silent failures

Do not swallow errors without a documented reason.

### Avoid hidden global state

Shared state should have a clear lifecycle and ownership model.

---

# Unity Contributions

The Unity project is part of the core Metaverse experience.

When modifying Unity content:

* Follow the existing project structure.
* Avoid unnecessary changes to ProjectSettings.
* Avoid committing generated or temporary files that are already excluded by `.gitignore`.
* Keep assets organized.
* Use meaningful asset names.
* Avoid duplicate assets.
* Do not unnecessarily reimport or modify unrelated assets.
* Test scenes after modifying gameplay-related systems.
* Verify platform-specific behavior when applicable.

When modifying prefabs or scenes, make sure the change does not unintentionally alter unrelated components.

For large Unity assets, avoid committing unnecessary duplicates or temporary exports.

---

# Server Contributions

The server is organized into distinct responsibilities including:

* Configuration
* Authentication
* Domain logic
* MongoDB persistence
* gRPC transport
* HTTP
* Realtime
* Room management
* Game Server Control
* Health and observability
* External service integrations

The current architecture starts from:

```text
src/index.js
```

The startup process validates configuration, establishes the database connection, prepares the public Lobby Room, initializes HTTP and realtime services, starts gRPC, and registers graceful shutdown.

Changes should preserve these lifecycle boundaries.

Do not bypass existing services or repositories without a strong architectural reason.

---

# Authentication

Authentication-related changes require additional care.

The authentication layer handles functionality such as:

* Registration
* Login
* Refresh
* Logout
* Logout from all devices
* User data retrieval
* Access and refresh tokens

When modifying authentication:

* Never log passwords.
* Never log access tokens.
* Never log refresh tokens.
* Never commit JWT secrets.
* Validate authentication consistently.
* Preserve token/session revocation behavior.
* Consider replay and session fixation risks.
* Add regression tests for security-sensitive behavior.

Authentication changes should receive careful review before merging.

---

# Realtime Contributions

Realtime communication is a critical part of the project.

The system supports multiple transports while sharing the same realtime core.

The general flow is:

```text
Transport
   ↓
RealtimeServer
   ↓
Flood Protection
   ↓
Envelope Parsing
   ↓
Acknowledgement Tracking
   ↓
Realtime Router
   ↓
Room / Presence / Game Services
   ↓
Serialization
   ↓
Transport Response
```

Realtime changes should preserve transport independence.

If possible, implement behavior inside the shared realtime core rather than duplicating the same behavior in:

```text
WebSocket
```

and:

```text
gRPC Streaming
```

Transport-specific behavior should remain inside the transport layer.

---

# Realtime Protocol

Realtime envelopes contain fields such as:

```text
id
type
channel
room
payload
```

When changing the protocol:

* Document the new field or message type.
* Consider backwards compatibility.
* Validate incoming data.
* Define error behavior.
* Consider malformed or malicious payloads.
* Update all affected clients.
* Test both supported realtime transports.

Do not silently change the meaning of an existing message.

If a protocol change is breaking, clearly document it in the Pull Request.

---

# Rooms and Presence

Room management maintains:

* Room membership
* Capacity
* Online counters
* Lobby state
* Connection state

When changing Room or Presence behavior:

* Verify join behavior.
* Verify leave behavior.
* Test unexpected disconnects.
* Test reconnects.
* Verify online counters.
* Check stale connection cleanup.
* Consider concurrent joins/leaves.
* Check broadcast behavior.

Avoid relying exclusively on client-side state for authoritative Room state.

---

# Dedicated Game Servers

Dedicated Game Server functionality includes:

* Server allocation
* Server health
* Session management
* Connection tickets
* Ticket validation
* Ticket consumption
* Warm Pool management

Security-sensitive ticket operations must preserve:

* Expiration validation
* Signature validation
* User association
* Room association
* Server association
* Replay protection

Do not weaken these checks to simplify local development.

---

# gRPC and Protobuf Changes

Protobuf contracts are part of the public communication boundary.

Existing contracts include authentication, health, and realtime streaming services.

When modifying `.proto` files:

* Consider backwards compatibility.
* Do not casually reuse field numbers.
* Do not remove fields without considering existing clients.
* Document breaking changes.
* Regenerate generated artifacts only when required by the project workflow.
* Test affected clients and services.

For a new field, prefer adding a new field rather than changing the semantic meaning of an existing field.

Never reuse a previously assigned Protobuf field number for an unrelated field.

---

# Database Changes

The project uses MongoDB through Mongoose repositories.

Database changes should consider:

* Existing production data
* Existing clients
* Indexes
* Migration requirements
* Backwards compatibility
* Validation
* Performance

Do not make destructive schema changes without a migration or a clearly documented upgrade strategy.

Repository access should remain inside the persistence layer.

Business logic should not directly depend on raw MongoDB operations unless the architecture explicitly requires it.

---

# Security

Security is a first-class concern.

The project contains authentication, JWT, TLS, realtime communication, internal services, and dedicated server allocation.

Never commit:

```text
.env
.env.production
JWT secrets
Private keys
TLS private keys
Database passwords
API keys
Access tokens
Refresh tokens
Service credentials
```

If a secret is accidentally committed:

1. Do not simply delete it in a new commit.
2. Assume the secret is compromised.
3. Rotate/revoke it immediately.
4. Notify the maintainers.
5. Follow the repository's `SECURITY.md`.

For vulnerabilities, use the private security reporting process rather than publicly exposing the vulnerability before a fix is available.

---

# Performance

Metaverse systems are sensitive to latency, memory usage, network traffic, and concurrent connections.

When making performance-sensitive changes, consider:

* CPU usage
* Memory usage
* Network traffic
* Serialization overhead
* Database queries
* Connection count
* Room size
* Broadcast frequency
* Allocation latency
* Garbage collection
* Client frame rate

Avoid premature optimization.

When claiming a performance improvement, provide measurements whenever possible.

For example:

```text
Before:
Average broadcast: 12ms

After:
Average broadcast: 7ms
```

---

# Testing

Every behavioral change should be validated.

Depending on the affected subsystem, testing may include:

### Server

* Unit tests
* Integration tests
* API tests
* Authentication tests
* Database tests
* Realtime tests

### Realtime

Test:

* Connect
* Disconnect
* Reconnect
* Join
* Leave
* Broadcast
* Invalid messages
* Unauthorized messages
* Flood protection
* Multiple concurrent clients

### Game Server

Test:

* Allocation
* Health checks
* Ticket creation
* Ticket validation
* Ticket expiration
* Ticket consumption
* Replay attempts
* Session lifecycle

### Unity

Test:

* Scene loading
* Gameplay behavior
* Networking
* UI
* Player state
* Platform-specific behavior

If automated tests do not exist for the affected area, describe the manual validation performed in the Pull Request.

---

# Documentation

Documentation is part of the implementation.

If a change modifies:

* Architecture
* API behavior
* Realtime protocol
* Configuration
* Environment variables
* Deployment
* Authentication
* Room behavior
* Dedicated server behavior

update the relevant documentation.

Do not leave documentation describing behavior that no longer exists.

---

# Environment Variables

Never commit real environment values.

Use safe examples such as:

```text
MONGODB_URI=mongodb://localhost:27017/example
JWT_SECRET=replace-with-a-local-development-secret
```

Documentation should explain:

* Variable name
* Purpose
* Whether it is required
* Example value
* Security sensitivity

Never place production credentials in documentation.

---

# Dependencies

Adding a dependency requires justification.

Before adding a new package, consider:

* Is the functionality already available?
* Is the dependency actively maintained?
* Does it introduce security risks?
* Does it significantly increase bundle or server size?
* Is its license compatible?
* Does it introduce unnecessary complexity?
* Can the functionality be implemented with the existing stack?

Do not add dependencies for trivial functionality.

Dependency upgrades should be tested separately when possible.

---

# Breaking Changes

Breaking changes require explicit documentation.

Examples include:

* Protobuf contract changes
* Realtime message changes
* Authentication behavior changes
* Database schema changes
* Environment variable removal
* Configuration changes
* Unity scene or prefab compatibility changes
* Client/server protocol changes

Mark breaking changes clearly in the Pull Request:

```text
BREAKING CHANGE
```

Explain:

1. What changed?
2. Why did it change?
3. Who is affected?
4. How should existing clients migrate?

---

# Review Process

Pull Requests are reviewed based on:

* Correctness
* Security
* Architecture
* Maintainability
* Performance
* Compatibility
* Test coverage
* Documentation
* Scope

Reviewers may request changes.

Please treat review comments as part of the engineering process rather than as personal criticism.

Contributors are encouraged to explain architectural decisions when a change is non-obvious.

---

# Maintainer Review

Maintainers may reject or request changes to a contribution when:

* It introduces unnecessary complexity.
* It conflicts with the existing architecture.
* It creates security risks.
* It introduces breaking changes without migration.
* It lacks sufficient testing.
* It duplicates existing functionality.
* It significantly reduces performance.
* It introduces an unnecessary dependency.
* It does not fit the project's scope.

A technically functional implementation is not automatically a good contribution.

The goal is to maintain a reliable and scalable Metaverse platform.

---

# What We Do Not Accept

The following contributions may be rejected:

* Malicious code
* Backdoors
* Credential harvesting
* Unauthorized tracking
* Deliberate vulnerabilities
* Hardcoded secrets
* Cryptocurrency mining
* Spam
* Unrelated large refactors
* Generated files that should not be committed
* Proprietary assets without appropriate rights
* Copyright-infringing content
* Dependencies with unacceptable security or licensing risks
* Breaking protocol changes without migration plans
* Changes that intentionally bypass authentication or authorization

---

# Third-Party Assets

Before contributing models, textures, audio, fonts, packages, or other third-party assets, make sure you have the legal right to distribute them.

Every third-party asset should have a compatible license.

When required, provide attribution.

Do not add assets copied from commercial games, websites, marketplaces, or other projects without appropriate permission.

---

# Large Files

Metaverse and Unity projects can contain large binary assets.

Before committing a large file:

* Verify that it is actually required.
* Check whether Git LFS or another storage mechanism should be used.
* Avoid committing temporary exports.
* Avoid duplicate assets.
* Avoid generated build outputs.

Do not commit:

```text
Build/
Builds/
Library/
Temp/
Logs/
obj/
.vscode/
.idea/
```

unless a specific project requirement explicitly states otherwise.

Follow the repository's `.gitignore`.

---

# Communication

Keep technical discussions:

* Clear
* Respectful
* Evidence-based
* Focused on the problem
* Focused on maintainability

When proposing a major change, explain the trade-offs.

Instead of:

```text
This implementation is better.
```

Prefer:

```text
This approach keeps the transport layer independent from the realtime core,
reduces duplicated logic, and allows both WebSocket and gRPC streaming
to share the same behavior.
```

---

# Contribution Checklist

Before opening a Pull Request:

* [ ] I searched for existing Issues and Pull Requests.
* [ ] My branch has a descriptive name.
* [ ] My changes are focused and related.
* [ ] I followed the existing architecture.
* [ ] I did not commit secrets or credentials.
* [ ] I did not commit unnecessary generated files.
* [ ] I tested the affected functionality.
* [ ] I considered backwards compatibility.
* [ ] I considered security implications.
* [ ] I considered performance implications.
* [ ] I updated documentation when necessary.
* [ ] My commit messages are descriptive.
* [ ] I reviewed my own diff.
* [ ] I explained important architectural decisions in the Pull Request.

---

# License

By contributing to this repository, you agree that your contributions will be licensed under the same license that governs the project, unless otherwise explicitly agreed by the project maintainers.

This repository currently includes an MIT License.

See:

```text
LICENSE
```

for the complete license text.

---

# Thank You

Every contribution helps improve Metaverse Rang World.

Whether you are fixing a small bug, improving documentation, optimizing realtime communication, improving the Unity experience, strengthening security, or contributing a major feature, your effort is appreciated.

Thank you for helping build a more reliable, scalable, and maintainable metaverse platform.

**Metaverse Rang World 3D Multiverse**
