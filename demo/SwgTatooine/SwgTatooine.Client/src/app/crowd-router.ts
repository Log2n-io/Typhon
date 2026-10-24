import type { CreatureKind } from '../data/server-config';
import type { CrowdBody } from '../render/layer-packer';

/**
 * Which crowd draws each body, and in what: the characters' crowd for a person — a player, a city NPC, a ninja — and a
 * species' own crowd for a beast.
 *
 * <b>The server says what a creature IS; this says which mesh that is.</b> A creature replicates its `template` and its
 * `skin`; the server's published table (`CreatureKind`) turns the pair into an asset id; and the id is either one of the
 * characters' outfits — a camp's ninjas and soldiers are dressed from the character pack — or a creature with a crowd of
 * its own. A body nobody dresses (template -1) is a civilian, and its crowd picks a civilian outfit by netId.
 *
 * <b>Strikes are counted here, not in the crowd</b>, because a blow is an EVENT and the crowd is drawn from state. Each
 * `Attack` the server streams adds one to its attacker's count; a crowd body whose count changed plays its attack once.
 * A count rather than a flag, so two blows between two frames are not one.
 *
 * Pure: the layers are whatever the caller hands in, and nothing here touches Babylon.
 */

/** Where one body goes: a layer, by index into the router's layers, and the outfit it wears there. */
interface Route {
  readonly layer: number;
  readonly outfit: number;
}

/** A body routed nowhere: a template whose asset no crowd could build. */
const NOWHERE: Route = { layer: -1, outfit: -1 };

/** The bodies one layer draws this frame. */
export interface RoutedLayer {
  readonly bodies: CrowdBody[];
  count: number;
}

export class CrowdRouter {
  /** One entry per layer: the characters first (index 0, when there are any), then each species. */
  readonly layers: RoutedLayer[];
  /** Bodies dropped this frame because their template's asset has no crowd; the HUD's to show, the packer's to know. */
  unrouted = 0;

  /** Per template, per skin. */
  private readonly routes: Route[][];
  /** Where a civilian goes: the characters' crowd, dressed by netId; nowhere when there is none. */
  private readonly civilian: Route;
  private readonly strikesByNetId = new Map<number, number>();

  /**
   * @param kinds The server's creature templates.
   * @param characterOutfits The characters' crowd's outfit ids, in its layer's order; `null` when it is not mounted.
   * @param species Each creature crowd's id, in layer order after the characters.
   */
  constructor(kinds: readonly CreatureKind[], characterOutfits: readonly string[] | null, species: readonly string[]) {
    const characters = characterOutfits === null ? -1 : 0;
    const first = characters + 1;
    this.civilian = characters < 0 ? NOWHERE : { layer: characters, outfit: -1 };
    this.layers = [];
    for (let i = 0; i < first + species.length; i++) {
      this.layers.push({ bodies: [], count: 0 });
    }

    this.routes = kinds.map((kind) =>
      kind.bodies.map((id): Route => {
        const outfit = characterOutfits?.indexOf(id) ?? -1;
        if (outfit >= 0) {
          return { layer: characters, outfit };
        }

        const s = species.indexOf(id);
        return s >= 0 ? { layer: first + s, outfit: 0 } : NOWHERE;
      }),
    );
  }

  /**
   * One blow, from the server's `Attack` event.
   *
   * @param attackerNetId Who struck.
   */
  strike(attackerNetId: number): void {
    this.strikesByNetId.set(attackerNetId, (this.strikesByNetId.get(attackerNetId) ?? 0) + 1);

    // Bounded by the bodies that ever fought in this session; dropped wholesale past a size no view reaches. A body whose
    // count resets plays one blow it did not strike, once — which is the whole cost of forgetting.
    if (this.strikesByNetId.size > STRIKE_MEMORY) {
      this.strikesByNetId.clear();
    }
  }

  /** Forgets every count: a `RESET` renumbers the world, and a count kept across it would belong to another body. */
  forget(): void {
    this.strikesByNetId.clear();
  }

  /**
   * Sorts this frame's bodies into their layers, dressing each.
   *
   * @param bodies The packer's pooled records.
   * @param count How many are this frame's.
   */
  route(bodies: readonly CrowdBody[], count: number): void {
    for (const layer of this.layers) {
      layer.count = 0;
    }

    this.unrouted = 0;
    for (let b = 0; b < count; b++) {
      const body = bodies[b];
      body.strikes = this.strikesByNetId.get(body.netId) ?? 0;
      let route: Route;
      if (body.template < 0) {
        route = this.civilian;
      } else if (body.template < this.routes.length && this.routes[body.template].length > 0) {
        // A template the table does not name, or one with no bodies, goes nowhere rather than reading past the table.
        const skins = this.routes[body.template];
        route = skins[body.skin % skins.length];
      } else {
        route = NOWHERE;
      }

      if (route.layer < 0) {
        this.unrouted++;
        continue;
      }

      body.outfit = route.outfit;
      const layer = this.layers[route.layer];
      if (layer.count < layer.bodies.length) {
        layer.bodies[layer.count] = body;
      } else {
        layer.bodies.push(body);
      }

      layer.count++;
    }
  }
}

/** How many attackers' counts are remembered before they are all forgotten. Several times any view's population. */
const STRIKE_MEMORY = 8192;
