// @ts-check

"use strict";

(() => {
    const messageEl = document.getElementById("message");
    const loginFormEl = document.getElementById("login-form");
    const usernameEl = document.getElementById("username");
    const passwordEl = document.getElementById("password");
    const createBtnEl = document.getElementById("create-account");
    const messageTemplateEl = document.getElementById("message-template");
    const antiforgeryToken = document.currentScript?.getAttribute("data-csrf-token");
    const pathBase = document.currentScript?.getAttribute("data-path-base");
    
    if (
        !(messageEl instanceof HTMLSpanElement) ||
        !(loginFormEl instanceof HTMLFormElement) ||
        !(usernameEl instanceof HTMLInputElement) ||
        !(passwordEl instanceof HTMLInputElement) ||
        !(createBtnEl instanceof HTMLInputElement) ||
        !(messageTemplateEl instanceof HTMLTemplateElement) ||
        typeof antiforgeryToken !== "string"
    ) {
        return;
    }

    /**
     * @param {string} message
     */
    const showMessage = (message) => {
        const clone = document.importNode(messageTemplateEl.content, true);
        if (!clone.firstChild) return;
        clone.firstChild.textContent = message;
        messageEl.replaceChildren(clone);
    };

    loginFormEl.addEventListener(
        "submit",
        (event) => {
            event.stopPropagation();
            event.preventDefault();

            const username = usernameEl.value;
            const password = passwordEl.value;

            if (!username || !password) {
                showMessage("Missing username or password");
                return;
            }

            if (event.submitter === createBtnEl) {
                (async () => {
                    const resp =
                        await fetch(
                            `${pathBase}/register`,
                            {
                                method: "post",
                                headers: {
                                    "Content-Type": "application/json",
                                    "X-XSRF-TOKEN": antiforgeryToken,
                                },
                                body: JSON.stringify({
                                    email: username,
                                    password,
                                })
                            }
                        );

                    if (!resp.ok) {
                        showMessage(
                            "There was something wrong with your username or password. Passwords must be 12 characters."
                        );
                        return;
                    }

                    showMessage(
                        "Your account was created! Contact an admin to activate it."
                    );
                })();
            } else {
                (async () => {
                    const resp =
                        await fetch(
                            `${pathBase}/login?useCookies=true`,
                            {
                                method: "post",
                                headers: {
                                    "Content-Type": "application/json",
                                    "X-XSRF-TOKEN": antiforgeryToken,
                                },
                                body: JSON.stringify({
                                    email: username,
                                    password,
                                })
                            }
                        );

                    if (!resp.ok) {
                        showMessage(
                            "There was something wrong with your username or password."
                        );
                        return;
                    }

                    showMessage("You have logged in!");
                    
                    const returnUrlParam = new URLSearchParams(window.location.search).get("ReturnUrl");
                    const returnUrl =
                        returnUrlParam ? new URL(pathBase + returnUrlParam, document.location.href) : null;
                    
                    const isReturnUrlLocal =
                        returnUrl &&
                        returnUrl.host === document.location.host &&
                        returnUrl.protocol === document.location.protocol;
                    
                    if (isReturnUrlLocal) {
                        setTimeout(() => { document.location.href = returnUrl.href; }, 1000);
                    } else {
                        setTimeout(() => document.location.reload(), 1000);
                    }
                })();
            }
        }
    );
})();
