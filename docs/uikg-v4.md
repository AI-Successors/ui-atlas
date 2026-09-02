# UI Knowledge Graph v4

`ui-atlas.uikg/4` retains the three grounding layers introduced by v2 and adds first-class causal `interaction` edges between Raw World states. Each edge identifies its source control, operation and attempt, actor, gesture, semantic action, outcome, order, frame references, diagnostic code, and evidence.

Successful actions point to observed result states. `NoChange`, `Failed`, `TimedOut`, and `Cancelled` actions point back to the source state and remain negative evidence. UIA-derived affordances are stored on controls as possible actions; an unobserved affordance never receives an invented destination. Equal routes from resumed recordings are merged by the read model while their session steps remain separate.

V1, v2, and v3 graphs are migrated to v4 with no fabricated interaction trace. The schemas are [uikg-v4.schema.json](../schemas/uikg-v4.schema.json), [uikg-export-v1.schema.json](../schemas/uikg-export-v1.schema.json), and [map-json-v2.schema.json](../schemas/map-json-v2.schema.json).
