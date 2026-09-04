/**
 * Where the API is.
 *
 * A production build defaults to the empty string, which makes every request relative and therefore
 * same-origin: the API process serves this bundle, so it is already the right host, whatever address the
 * workstation answers on. Baking one in would mean a build that only runs on the machine it was built for.
 *
 * `npm run dev` serves the client on its own port and has to be told where the API is, so the development
 * default points at the local one. `VITE_API_URL` overrides either, which is how the browser journeys aim at
 * their own isolated instance.
 *
 * This lives in its own module because more than one place needs it now. It was App.tsx's private constant
 * until the instance badge had to reach the same origin, and a second copy of "where is the API" is the kind
 * of duplication that stays right for exactly as long as nobody edits either one.
 */
export const API_ORIGIN: string =
  import.meta.env.VITE_API_URL ?? (import.meta.env.DEV ? "http://127.0.0.1:5080" : "");
