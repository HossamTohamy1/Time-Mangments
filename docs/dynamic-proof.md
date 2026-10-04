# Dynamic proof — nothing about the institution is hard-coded

The product must serve universities, schools and institution types nobody anticipated. This document lists where each
business concept lives (always **data**, never an enum or a code branch) and walks through the automated acceptance test
that proves it end to end.

## Where concepts live

| Concept | Stored as | Edited in | Consumed by |
| --- | --- | --- | --- |
| Session types (lecture, lab, conversation club, …) incl. default duration, default room type, shareable, counts toward load, allowed instructor types / days / periods, colour | `SessionTypes` lookup rows | Settings › Lookups | engine (unary rules), editor colours, exports |
| Instructor types, room types, group kinds, org-unit types, equipment tags | lookup rows | Settings › Lookups (merge / deactivate, usage-blocked delete) | validation, rules, filters |
| Organisational hierarchy (faculty › department …, grade › class …) | `OrgUnits` + `StudentGroups.ParentGroupId` | Settings › Organisation, Groups | conflict sets (a class conflicts with its cohort and its sub-groups) |
| Words shown to users ("Section", "Class", "Cohort", "Trainer", …) | `TerminologyOverrides` per language | Settings › Terminology | UI (`term` pipe), PDF / Excel / CSV headings |
| Week structure (working days, week start, periods, breaks, shifts, per-day overrides, A/B cycles) | `TimeStructure` | Settings › Time structure (with impact preview) | grid, engine, self-service dates |
| Constraints: severity (off / soft / hard), weight, parameters | `ConstraintSettings` | Settings › Constraints (with impact preview) | validator, CP-SAT domains, heuristic, verifier |
| Custom rules ("CONV only after the break", "max 2 labs per day per group", …) | `RuleDefinitions` (JSON rule model) | Rule Builder (live preview + impact) | same engine as built-ins |
| Extra attributes (e.g. room "acoustic treatment") | `CustomFieldDefinitions` + JSON values | Settings › Custom fields; import columns `cf.<key>` | forms, lists, rule scopes, imports |
| Features (pools, week cycles, shifts, substitutions, …) | `FeatureFlags` | Settings › Features | API guards, navigation, forms |
| Roles & permissions | `Roles` with permission codes, per-institution assignments | Settings › Roles / Users | authorisation policies, navigation |
| Whole starting configuration | template bundles (JSON) | setup wizard, save-as-template, export / import | institution provisioning |

Only purely technical states are enums (schedule status, constraint severity, job status, custom-field data type).

## The engine reads configuration, not code paths

- Constraints only *report* violations with an amount; whether a finding is hard, soft (× weight) or ignored comes from the
  institution's configuration. Core integrity constraints (double booking, slot structure) are always hard.
- The same `ConstraintConfiguration` drives manual validation (drag & drop colours and reasons), impact analysis,
  CP-SAT generation (domains are computed by evaluating every configured constraint), the heuristic and the verifier.
- Rule Builder rules compile to the same constraint interface as the built-ins.

## Acceptance test: a language training centre

`backend/tests/Timetable.Api.IntegrationTests/AcceptanceLanguageCenterTests.cs` builds an institution type that exists in
no template, using only public API calls, and asserts the result:

1. **Create** "Language Training Center" from the *Blank* template (Arabic as default language).
2. **Vocabulary**: room types *Conversation studio* / *Classroom*; instructor types *Native-speaker trainer* / *Grammar tutor*;
   group kind *Cohort*; session types *Conversation club* (1 period, studio, natives only) and *Grammar workshop*
   (2 periods, classroom). Terminology: group → *Cohort / دفعة*, instructor → *Trainer / مدرب*, course → *Program / برنامج*.
3. **Feature flag**: instructor pools switched on (trainers are chosen by the engine).
4. **Time**: evening periods 17:00–21:15 with a tea break, Saturday–Wednesday working week.
5. **Custom field** on rooms (*acoustic treatment*) and a **no-code rule**: conversation clubs only after the break
   (periods 4–5); a weight change for group gaps.
6. **Import** the rooms from CSV, including the `cf.acoustic` column.
7. **People and plan**: trainers, tutors, two programs, three evening cohorts, a term, grammar and conversation sessions.
8. **Effective configuration** reflects the terminology, session types and periods.
9. **Readiness** reports no blocking issue; **generation** (CP-SAT) places all 12 weekly occurrences; every conversation club
   starts after the break and only on Saturday–Wednesday; the conflicts report has no hard finding.
10. **Manual edit** honours the same rule: moving a conversation club before the break is refused with `RULE_SLOT_RANGE`.
11. **Publish** and **export**: Arabic PDF renders; the CSV export uses the institution's own words (*Program*, *Cohorts*,
    *Conversation club*).

No line of product code mentions any of these concepts. Running the test:

```bash
cd backend && dotnet test --filter AcceptanceLanguageCenterTests
```

## Other places the claim is exercised

- Two seeded institutions with different templates (university and secondary school) run side by side; integration tests
  validate constraints, exports and generation for both.
- Phase 3–7 integration tests cover usage-blocked deletes, lookup merge, terminology, feature-flag gating, custom-field
  validation, rule preview and impact analysis.
- The Playwright suite runs the same flows in English and Arabic, light and dark.
