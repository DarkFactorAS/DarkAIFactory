#
# DFAgent

This repository contains shared agent instructions and skills, plus a Dockerized
Jira coding agent in [aiteam/aiworker](aiteam/aiworker/README.md).

## Add as a project submodule

From the project root, run:

```sh
git submodule add https://github.com/DarkFactorAS/AIAgents.git .agents
```

If the submodule is already configured, initialize or update it with:

```sh
git submodule update --init --recursive
```

## Run the Jira coding agent

See the [AI worker README](aiteam/aiworker/README.md) for setup, configuration,
and Docker Compose instructions.
