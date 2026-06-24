# Game Design Pattern Roadmap Spec

## Goal

Produce a concrete, project-specific architectural roadmap that guides future refactors in `dream` using proven game-design and game-engine structure patterns.

## Intended Audience

- current project maintainer
- future contributors
- agentic workers executing refactor plans later

## Deliverable Requirements

The roadmap should:

- identify the biggest current structural bottlenecks
- name the relevant patterns explicitly
- prioritize them by near-term value
- connect the advice to existing files in this repository
- avoid recommending unnecessary large-framework rewrites

## Content Requirements

### Must include

- current architecture assessment
- best-fit patterns
- anti-patterns or risks
- phased roadmap
- file-level focus areas for each phase

### Must avoid

- generic textbook pattern summaries with no repo mapping
- recommending ECS as the immediate next move
- proposing large rewrites with no incremental path

## Phased Roadmap Expectations

The roadmap should define phases that are practical to execute incrementally:

1. gameplay shell and scene/state boundaries
2. player decomposition
3. world and physics decomposition
4. renderer decomposition
5. optional later command/event/factory cleanup

## Success Criteria

A reader should be able to answer:

- what is structurally wrong today
- what pattern solves each problem
- what order to tackle them in
- which files are likely to move or split in each phase
