    // Copyright 2026 Küstenlogik · Apache-2.0
    // ------------------------------------------------------------------
    // #306 / #314 — Security rail JS fragment.
    //
    // Moved out of core (render-sidebar.js / render-main.js) into the
    // Kuestenlogik.Bowire.Security.Scanner package. Both renderers are
    // thin shells: the sidebar is a title + hint, and the main pane
    // hosts the security panel the AI package contributes via
    // window.__bowireAi.renderSecurityPanel(). When the AI package isn't
    // in the workbench process, the main pane degrades to an install
    // hint. Registered on the renderer-key seam so core resolves them
    // from the descriptor's Sidebar/MainPaneRendererKey — core no longer
    // names 'security' in its dispatch.
    // ------------------------------------------------------------------

    function renderSecuritySidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        sidebar.appendChild(renderSidebarToolbar({ title: 'Security' }));
        sidebar.appendChild(el('div', {
            className: 'bowire-pane-empty',
            style: 'padding:12px 14px',
            textContent: 'Threat model, fuzz, and Nuclei templates sit in the main pane. Discovered endpoints are pulled automatically from the active workspace.'
        }));
        return sidebar;
    }

    function renderSecurityMain() {
        var secMain = el('div', { id: 'bowire-main-security', className: 'bowire-main bowire-main-security' });
        if (typeof window !== 'undefined' && window.__bowireAi
                && typeof window.__bowireAi.renderSecurityPanel === 'function') {
            // Wrap in the shared main-pad gutter so the security surface
            // aligns with every other rail's left/right inset instead of
            // sitting at the pane edge.
            var secWrap = el('div', { className: 'bowire-main-pad' });
            secWrap.appendChild(window.__bowireAi.renderSecurityPanel());
            secMain.appendChild(secWrap);
        } else {
            secMain.appendChild(el('p', {
                className: 'bowire-pane-empty bowire-main-pad',
                textContent: 'Security tools need Kuestenlogik.Bowire.Ai installed in the workbench process. Install the package + restart, or switch back to Discover via the rail.'
            }));
        }
        return secMain;
    }

    if (typeof window !== 'undefined') {
        window.__bowireRailRenderers = window.__bowireRailRenderers || {};
        window.__bowireRailRenderers.securitySidebar = renderSecuritySidebar;
        window.__bowireRailRenderers.securityMain = renderSecurityMain;
    }
