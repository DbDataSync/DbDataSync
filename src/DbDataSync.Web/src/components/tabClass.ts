/** The class a routed tab wears, lit by whether its route is the current one. Its own module because a
 * component file that also exports a helper breaks fast refresh. */
export const tabClass = ({ isActive }: { isActive: boolean }) => `tab ${isActive ? 'active' : ''}`
