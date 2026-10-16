/**
 * #1639: whether the signed-in person holds system administration (accounts, grants, identity links, review
 * configuration, diagnostics). Every global administrator does; the built-in `admin` also holds the separate
 * authority override, which `isAdministrator` reports. A session from a server without the split reports
 * only `isAdministrator`, which then means both.
 */
export const isSystemAdministrator = (user: { isAdministrator: boolean; isSystemAdministrator?: boolean }) =>
  user.isSystemAdministrator ?? user.isAdministrator;
