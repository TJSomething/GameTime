// @ts-check

(() => {
    const search = document.getElementById("search");
    const results = document.getElementById("results");
    const token = document.currentScript?.getAttribute("data-token");
    
    if (!(results instanceof HTMLUListElement) || !(search instanceof HTMLInputElement) || !token) return;
    
    /**
     * @type {number | undefined}
     */
    let debounceTimer;
    let latestJobTime = 0;

    /**
     * @param {() => void} action
     * @param {number} time
     */
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
    
    /** @typedef {{id: string, name: string, year: string|null}} Game */

    /**
     * @param {string} rawXml
     * @returns {Game[]}
     */
    function extractGames(rawXml) {
        const parser = new DOMParser();
        const parsedXml = parser.parseFromString(rawXml, "text/xml");

        const itemNodes = Array.from(parsedXml.getElementsByTagName("item"));

        return itemNodes.flatMap(node => {
            let id = node.getAttribute("id");
            let name = node.getElementsByTagName("name")[0]?.getAttribute("value");
            let year = node.getElementsByTagName("yearpublished")[0]?.getAttribute("value") ?? null;

            if (!id || !name) {
                return [];
            }

            return ([{
                id,
                name,
                year,
            }]);
        });
    }

    /**
     * @param params {{query: string, exact?: boolean}}
     * @returns {Promise<Game[]>}
     */
    const searchForGame = async (params) => {
        const url = new URL("https://boardgamegeek.com/xmlapi2/search?type=boardgame");
        url.searchParams.set("query", params.query);
        if (params.exact) {
            url.searchParams.set("exact", "1");
        }

        const resp = await fetch(url, {
            method: "GET",
            mode: "cors",
            headers: {
                "Authorization": `Bearer ${token}`,
            },
        });
        const rawXml = await resp.text();
        
        return extractGames(rawXml);
    }

    /**
     * 
     * @param {string} id
     * @returns {Promise<Game[]>}
     */
    const getGameById = async (id) => {
        const url = new URL("https://boardgamegeek.com/xmlapi2/thing?type=boardgame");
        url.searchParams.set("id", id);

        const resp = await fetch(url);
        const rawXml = await resp.text();
        
        return extractGames(rawXml);
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
            link.href = `game/${Number(item.id)}`;
            link.appendChild(document.createTextNode(`${item.name} (${item.year})`));
            elem.appendChild(link);
            results.appendChild(elem);
        }

        results.removeAttribute("aria-busy");
    };

    search.addEventListener("keydown", () => debounce(onSearch, 500));
})();
