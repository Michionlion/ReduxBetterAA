# Decision 0030: persistent foliage repair and independent map-view AA

## Status

Accepted for 0.5.26.

## Context

The public Sharpness and TAA stability sliders were registered with an empty
Redux range-format string, so their value labels were blank while dragging.
The foliage motion source repair was available only in the Ctrl+F10 engineering
panel and did not persist. Map-view reconstruction can also be less useful than
flight reconstruction, but changing the global AA mode to Off discarded the
user's desired flight setting.

## Decision

- Numeric public sliders use ReduxLib's two-decimal `"{0:F2}"` display format.
- Foliage motion repair is a persistent user-facing toggle, enabled by default.
  It remains the same control exposed in Ctrl+F10 and may improve performance
  when the compatible indirect rendering path is available.
- Map-view AA is a separate persistent toggle, enabled by default to preserve
  existing behavior. When disabled, the coordinator activates its truthful Off
  backend only for `Map3DView` while retaining the requested global mode.
- Leaving map view automatically restores the retained global mode through the
  normal camera-graph refresh and backend lifecycle.
- Schema 21 records `mapViewAaEnabled` and `mapViewAaOverrideActive`; the
  intentional map override is not reported as a backend fallback failure.

## Lifecycle

Changing the map policy while map view is active deactivates the current
backend, releases its scene resources through the normal idempotent path, and
rediscovers/configures the effective backend. Changing it outside map view does
not disturb the active backend. Changing foliage repair resets temporal history
only when the renderer behavior actually changes.

## Verification

Focused EditMode coverage verifies that disabling map AA maps any requested
backend to Off only for the map scene. Release verification must additionally
confirm that both normal sliders display numbers, both toggles persist, map Off
does not change the stored flight mode, and returning to flight restores that
mode.
