(() => {
    const search = document.getElementById("search");
    const results = document.getElementById("results");
    let debounceTimer;
    let latestJobTime = 0;

    const debounce = (action, time) => {
        if (debounceTimer) {
            clearTimeout(debounceTimer);
            debounceTimer = undefined;
        }

        debounceTimer = setTimeout(() => {
            action();
            clearTimeout(debounceTimer);
            debounceTimer = undefined;
        }, time);
    };

    /**
     * @param params {{query: string, exact: boolean|undefined}}
     * @returns {Promise<{id: string, name: string}[]>}
     */
    const searchForGame = async (params) => {
        const url = new URL("https://boardgamegeek.com/xmlapi2/search?type=boardgame");
        url.searchParams.set("query", params.query);
        if (params.exact) {
            url.searchParams.set("exact", "1");
        }

        const resp = await fetch(url);
        const rawXml = await resp.text();
        const parser = new DOMParser();
        const parsedXml = parser.parseFromString(rawXml, "text/xml");

        const itemNodes = Array.from(parsedXml.getElementsByTagName("item"));

        return itemNodes.map(node => ({
            id: node.getAttribute("id"),
            name: node.getElementsByTagName("name")[0].getAttribute("value"),
        }));
    }
    
    const getGameById = async (id) => {
        const url = new URL("https://boardgamegeek.com/xmlapi2/thing?type=boardgame");
        url.searchParams.set("id", id);

        const resp = await fetch(url);
        const rawXml = await resp.text();
        const parser = new DOMParser();
        const parsedXml = parser.parseFromString(rawXml, "text/xml");

        const itemNodes = Array.from(parsedXml.getElementsByTagName("item"));

        return itemNodes.map(node => ({
            id: node.getAttribute("id"),
            name: node.getElementsByTagName("name")[0].getAttribute("value"),
        }));
    }

    const onSearch = async () => {
        const jobStart = Date.now();
        latestJobTime = jobStart;
        
        results.innerHTML = "";

        if (!search.value) {
            return;
        }

        results.setAttribute("aria-busy", "true");
        const query = search.value;
        let items = await searchForGame({ query });
        
        // Handle small searches
        if (items.length > 50) {
            const exactMatches = await searchForGame({ query, exact: true });
            items.unshift(...exactMatches);
        }
        
        // Also handle literal IDs
        if (Number(query).toString() === query) {
            const idMatches = await getGameById(query);
            items.unshift(...idMatches);
        }
        
        // Deduplicate
        items = (() => {
            const newItems = [];
            const ids = new Set();
            
            for (const item of items) {
                if (!ids.has(item.id)) {
                    ids.add(item.id);
                    newItems.push(item);
                }
            }
            
            return newItems;
        })();
        
        // Abort if a newer request was issued
        if (jobStart < latestJobTime) {
            return;
        }

        for (const item of items.slice(0, 100)) {
            const elem = document.createElement("li");
            const link = document.createElement("a");
            link.href = `game/${item.id|0}`;
            link.appendChild(document.createTextNode(item.name));
            elem.appendChild(link);
            results.appendChild(elem);
        }

        results.removeAttribute("aria-busy");
    };

    search.addEventListener("keydown", () => debounce(onSearch, 500));
})();
