    // ---- Freeform request: the /api/invoke body ----
    // The classic freeform form (render-main.js) builds its call here, so
    // what reaches the plugin is decided in one small, testable place.

    /**
     * The JSON body the freeform form posts to /api/invoke.
     *
     * Metadata goes to every protocol, not only ad-hoc REST (#996): a
     * plugin that takes its addressing from metadata — SOAP's soap_action
     * and target_namespace, gRPC's headers — cannot be called by hand
     * otherwise, and the form's Metadata tab is shown for every protocol.
     * An empty map is sent as null, as before.
     *
     * @param {object} fr          The freeform request state.
     * @param {string} bodyToSend  The payload, already defaulted to '{}'.
     * @param {boolean} verbHasBody  False for a REST GET/HEAD/OPTIONS.
     * @returns {object} service, method, messages, metadata, protocol.
     */
    function buildFreeformInvokeBody(fr, bodyToSend, verbHasBody) {
        var metadata = (fr.metadata && typeof fr.metadata === 'object' && Object.keys(fr.metadata).length > 0)
            ? fr.metadata : null;
        return {
            service: fr.service,
            method: fr.method,
            messages: [verbHasBody ? bodyToSend : '{}'],
            metadata: metadata,
            protocol: fr.protocol
        };
    }
