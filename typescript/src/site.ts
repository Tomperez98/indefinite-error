/**
 * `indefinite(name, fn)`: mark a function as an operation whose outcome can
 * be indefinite.
 *
 * @module
 */

import { assert, checkSite, ignore, show } from "./core.ts";
import { current, type Draw, type Fault, type IndefiniteFault, type Injection } from "./injection.ts";

// `any` is what lets `F` be any function, `this` and overloads included:
// ClassMethodDecoratorContext constrains its method type the same way.
type AnyFunction = (this: any, ...args: any[]) => any;

/** A method decorator, as TC39 standard decorators define it. */
export type MethodDecorator = <This, F extends (this: This, ...args: any) => any>(
  method: F,
  context: ClassMethodDecoratorContext<This, F>,
) => F;

/** Where a wrapper keeps its site, so wrapping it again is caught. */
const SITE = Symbol("indefinite.site");

const PASS: Draw = { kind: "pass" };

/**
 * Marks `fn` as an operation whose outcome can be indefinite: a database
 * commit, a call to another service, a message you publish. Returns a
 * function with `fn`'s exact type.
 *
 * ```ts
 * const commit = indefinite("db.commit", (tx: Tx) => tx.commit());
 * ```
 *
 * Outside a request that `withIndefinite` seeded, the wrapper just calls `fn`.
 * Inside one, each call either passes, returning or throwing what `fn` did,
 * or faults:
 *
 * - **before**: `fn` never runs, and the request ends.
 * - **after**: `fn` runs to completion (its promise settles), its outcome is
 *   discarded, and the request ends: it happened, and nobody was told.
 *
 * A fault throws, or rejects if `fn` is an `async` function or returned a
 * promise. Once one fires, every later marked call in the request throws it
 * again without running, and `withIndefinite` answers `500` whatever the
 * handler does with it.
 *
 * `name` is the site: it goes on the fault line, and each name draws its own
 * faults. Two wrappers with one name share one fault stream. It must be
 * non-empty and printable, with no whitespace.
 *
 * @throws {TypeError} If `name` isn't a valid site name, `fn` isn't a
 *   function, `fn` is a generator, or `fn` is already marked.
 */
export function indefinite<F extends AnyFunction>(name: string, fn: F): F;

/**
 * The same, as a decorator for a class method:
 *
 * ```ts
 * class Ledger {
 *   @indefinite("ledger.add")
 *   async add(amount: number) { ... }
 * }
 * ```
 */
export function indefinite(name: string): MethodDecorator;

export function indefinite(name: string, ...fn: [AnyFunction?]): AnyFunction | MethodDecorator {
  checkSite(name);
  if (fn.length === 0) {
    return ((method: AnyFunction, context: ClassMethodDecoratorContext | undefined) => {
      if (context?.kind !== "method") {
        throw new TypeError(
          `indefinite: indefinite(${show(name)}) decorates class methods; to mark a function, call indefinite(${show(name)}, fn)`,
        );
      }
      return wrap(name, method);
    }) as MethodDecorator;
  }
  return wrap(name, fn[0]);
}

function wrap(site: string, fn: unknown): AnyFunction {
  if (typeof fn !== "function") {
    throw new TypeError(`indefinite: ${show(site)} needs a function, got ${show(fn)}`);
  }
  const applied: unknown = (fn as { [SITE]?: string })[SITE];
  if (applied !== undefined) {
    throw new TypeError(`indefinite: ${show(site)} wraps a function already marked ${show(applied)}`);
  }
  const kind = Object.prototype.toString.call(fn);
  if (kind === "[object GeneratorFunction]" || kind === "[object AsyncGeneratorFunction]") {
    throw new TypeError(`indefinite: generators aren't supported: ${show(site)}`);
  }
  const rejects = kind === "[object AsyncFunction]";
  const target = fn as AnyFunction;
  const wrapper = function (this: unknown, ...args: unknown[]): unknown {
    return call(site, target, this, args, rejects);
  };
  Object.defineProperties(wrapper, {
    name: { value: target.name, configurable: true },
    length: { value: target.length, configurable: true },
    [SITE]: { value: site },
  });
  return wrapper;
}

function call(site: string, fn: AnyFunction, self: unknown, args: unknown[], rejects: boolean): unknown {
  const injection = current();
  const draw = injection === undefined ? PASS : injection.next(site);
  if (draw.kind === "pass") {
    return fn.apply(self, args);
  }
  if (draw.kind === "over") {
    return fail(draw.error, rejects);
  }
  const { fault } = draw;
  const inj = injection as Injection; // only an injection draws a fault
  if (fault.phase === "before") {
    return fail(ended(inj, fault), rejects);
  }
  let result: unknown;
  try {
    result = fn.apply(self, args);
  } catch {
    throw ended(inj, fault); // it ran and threw: that outcome is what gets lost
  }
  if (!isThenable(result)) {
    throw ended(inj, fault);
  }
  // It happened once it settles; by then the request may have ended.
  const settle = () => {
    const error = inj.abort(fault);
    if (error === undefined) {
      return result; // nothing left to fault: its real outcome
    }
    throw error;
  };
  return handled(Promise.resolve(result).then(settle, settle));
}

/** The fault that ends the request, for a call the request is still open for. */
function ended(injection: Injection, fault: Fault): IndefiniteFault {
  const error = injection.abort(fault);
  assert(error !== undefined, "a fault drawn in a closed injection"); // next() just drew it
  return error;
}

function fail(error: IndefiniteFault, rejects: boolean): Promise<never> {
  if (rejects) {
    return handled(Promise.reject(error));
  }
  throw error;
}

/**
 * Marks a fault's rejection as handled, and returns the same promise: a
 * caller that awaits it still sees the rejection, but one that drops it
 * doesn't crash the process. `withIndefinite` answers for the fault anyway.
 */
function handled<T>(promise: Promise<T>): Promise<T> {
  promise.catch(ignore);
  return promise;
}

function isThenable(value: unknown): value is PromiseLike<unknown> {
  return (
    (typeof value === "object" || typeof value === "function") &&
    value !== null &&
    typeof (value as { then?: unknown }).then === "function"
  );
}
