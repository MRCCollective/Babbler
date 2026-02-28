const statusEl = document.getElementById("status");
        const sourceLanguageEl = document.getElementById("sourceLanguage");
        const targetLanguageEl = document.getElementById("targetLanguage");
        const micInputEl = document.getElementById("micInput");
        const startBtn = document.getElementById("startBtn");
        const stopBtn = document.getElementById("stopBtn");
        const clearBtn = document.getElementById("clearBtn");
        const partialEl = document.getElementById("partial");
        const translatedPartialEl = document.getElementById("translatedPartial");
        const transcriptEl = document.getElementById("transcript");
        const speechSdk = window.SpeechSDK;

        let recognizer = null;
        let running = false;
        let selectedMicDeviceId = "";

        if (!speechSdk) {
            statusEl.textContent = "Status: Speech SDK failed to load.";
            throw new Error("Speech SDK failed to load.");
        }

        startBtn.addEventListener("click", async () => {
            if (running) {
                return;
            }

            try {
                await startRecognition();
            } catch (err) {
                addSystemLine(`Start failed: ${err.message || err}`);
                setStatus("idle");
            }
        });

        stopBtn.addEventListener("click", async () => {
            await stopRecognition();
        });

        clearBtn.addEventListener("click", () => {
            transcriptEl.replaceChildren();
            partialEl.textContent = "";
            translatedPartialEl.textContent = "";
        });

        micInputEl.addEventListener("change", () => {
            selectedMicDeviceId = getSelectedMicDeviceId();
            addSystemLine(`Mic selected: ${getSelectedMicLabel()}`);
        });

        async function init() {
            setStatus("initializing");
            await ensureMicrophonePermission();
            await loadMicrophones(true);
            addSystemLine("Ready.");
            setStatus("idle");
        }

        function addSystemLine(message) {
            const line = document.createElement("p");
            line.className = "line system";
            line.textContent = `[system] ${message}`;
            transcriptEl.prepend(line);
        }

        function addFinalLine(message) {
            const line = document.createElement("p");
            line.className = "line";
            line.textContent = message;
            transcriptEl.prepend(line);
        }

        function setStatus(state) {
            if (state === "running") {
                statusEl.textContent = `Status: listening (${sourceLanguageEl.value})`;
                startBtn.disabled = true;
                stopBtn.disabled = false;
                sourceLanguageEl.disabled = true;
                targetLanguageEl.disabled = true;
                micInputEl.disabled = true;
                return;
            }

            if (state === "initializing") {
                statusEl.textContent = "Status: initializing...";
                startBtn.disabled = true;
                stopBtn.disabled = true;
                sourceLanguageEl.disabled = true;
                targetLanguageEl.disabled = true;
                micInputEl.disabled = true;
                return;
            }

            statusEl.textContent = "Status: idle";
            startBtn.disabled = false;
            stopBtn.disabled = true;
            sourceLanguageEl.disabled = false;
            targetLanguageEl.disabled = false;
            micInputEl.disabled = false;
        }

        function normalizeMicDeviceId(value) {
            const normalized = (value || "").trim();
            if (!normalized ||
                normalized.toLowerCase() === "default" ||
                normalized.toLowerCase() === "communications") {
                return "";
            }

            return normalized;
        }

        function getSelectedMicDeviceId() {
            return normalizeMicDeviceId(micInputEl.value);
        }

        function getSelectedMicLabel() {
            const option = micInputEl.options[micInputEl.selectedIndex];
            return option?.textContent?.trim() || "Default microphone";
        }

        function canonicalizeMicLabel(label) {
            let value = (label || "").trim().toLowerCase();
            while (value.startsWith("default - ")) {
                value = value.slice("default - ".length).trim();
            }
            return value;
        }

        function resolveDefaultAliasMicDeviceId() {
            const selectedId = getSelectedMicDeviceId();
            if (selectedId) {
                return selectedId;
            }

            const selectedCanonicalLabel = canonicalizeMicLabel(getSelectedMicLabel());
            if (!selectedCanonicalLabel) {
                return "";
            }

            const match = Array.from(micInputEl.options).find(option => {
                const optionDeviceId = normalizeMicDeviceId(option.value);
                if (!optionDeviceId) {
                    return false;
                }

                const optionCanonicalLabel = canonicalizeMicLabel(option.textContent || "");
                if (!optionCanonicalLabel) {
                    return false;
                }

                return optionCanonicalLabel === selectedCanonicalLabel ||
                    optionCanonicalLabel.includes(selectedCanonicalLabel) ||
                    selectedCanonicalLabel.includes(optionCanonicalLabel);
            });

            return normalizeMicDeviceId(match?.value || "");
        }

        async function ensureMicrophonePermission() {
            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                throw new Error("Browser does not support getUserMedia.");
            }

            const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
            for (const track of stream.getTracks()) {
                track.stop();
            }
        }

        async function loadMicrophones(preserveSelection = false) {
            const previousValue = preserveSelection ? getSelectedMicDeviceId() : "";
            const devices = await navigator.mediaDevices.enumerateDevices();
            const defaultDevice = devices.find(device =>
                device.kind === "audioinput" &&
                (device.deviceId || "").toLowerCase() === "default");
            const microphones = devices.filter(device =>
                device.kind === "audioinput" &&
                normalizeMicDeviceId(device.deviceId));

            micInputEl.innerHTML = "";

            const defaultOption = document.createElement("option");
            defaultOption.value = "";
            defaultOption.textContent = defaultDevice?.label
                ? `Default - ${defaultDevice.label}`
                : "System default microphone";
            micInputEl.appendChild(defaultOption);

            microphones.forEach((device, index) => {
                const option = document.createElement("option");
                option.value = normalizeMicDeviceId(device.deviceId);
                option.textContent = device.label || `Microphone ${index + 1}`;
                micInputEl.appendChild(option);
            });

            const canRestore = previousValue &&
                Array.from(micInputEl.options).some(option => normalizeMicDeviceId(option.value) === previousValue);
            if (canRestore) {
                micInputEl.value = previousValue;
            }

            selectedMicDeviceId = getSelectedMicDeviceId();
        }

        async function getSpeechToken() {
            const response = await fetch("/api/speech/token");
            const raw = await response.text();
            if (!response.ok) {
                throw new Error(raw || `Token request failed (${response.status})`);
            }

            const data = raw ? JSON.parse(raw) : null;
            if (!data?.token || !data?.region) {
                throw new Error("Token response was invalid.");
            }

            return data;
        }

        function getSpeechServiceResultJson(result) {
            const propertyId = speechSdk?.PropertyId?.SpeechServiceResponse_JsonResult;
            if (!propertyId || !result?.properties?.getProperty) {
                return null;
            }

            const raw = result.properties.getProperty(propertyId);
            if (!raw) {
                return null;
            }

            try {
                return JSON.parse(raw);
            } catch {
                return null;
            }
        }

        function extractRecognizedText(result) {
            const direct = (result?.text || "").trim();
            if (direct) {
                return direct;
            }

            const json = getSpeechServiceResultJson(result);
            if (!json || typeof json !== "object") {
                return "";
            }

            const display = (json.DisplayText || "").trim();
            if (display && display.toLowerCase() !== "success") {
                return display;
            }

            const bestDisplay = (json?.NBest?.[0]?.Display || "").trim();
            if (bestDisplay && bestDisplay.toLowerCase() !== "success") {
                return bestDisplay;
            }

            const lexical = (json?.NBest?.[0]?.Lexical || "").trim();
            if (lexical && lexical.toLowerCase() !== "success") {
                return lexical;
            }

            return "";
        }

        function extractTranslatedText(result, targetLanguage) {
            const target = (targetLanguage || "").trim().toLowerCase();
            if (!target) {
                return "";
            }

            const direct = (result?.translations?.get?.(target) || "").trim();
            if (direct) {
                return direct;
            }

            const json = getSpeechServiceResultJson(result);
            if (!json || typeof json !== "object") {
                return "";
            }

            const readText = value => {
                if (!value) {
                    return "";
                }
                if (typeof value === "string") {
                    return value.trim();
                }
                if (typeof value !== "object") {
                    return "";
                }
                return (
                    (value.DisplayText || "").trim() ||
                    (value.displayText || "").trim() ||
                    (value.Text || "").trim() ||
                    (value.text || "").trim() ||
                    (value.Translation || "").trim() ||
                    (value.translation || "").trim() ||
                    "");
            };

            const addFromArray = arrayValue => {
                if (!Array.isArray(arrayValue)) {
                    return "";
                }

                for (const entry of arrayValue) {
                    const language = (entry?.Language || entry?.language || entry?.To || entry?.to || "")
                        .toString()
                        .trim()
                        .toLowerCase();
                    if (language !== target) {
                        continue;
                    }

                    const text = readText(entry);
                    if (text) {
                        return text;
                    }
                }

                return "";
            };

            const addFromObject = objectValue => {
                if (!objectValue || typeof objectValue !== "object") {
                    return "";
                }

                const directValue = readText(objectValue[target]);
                if (directValue) {
                    return directValue;
                }

                for (const [key, value] of Object.entries(objectValue)) {
                    const normalizedKey = (key || "").trim().toLowerCase();
                    if (normalizedKey === target) {
                        const text = readText(value);
                        if (text) {
                            return text;
                        }
                    }
                }

                return "";
            };

            const fromTopLevel =
                addFromObject(json.Translations) ||
                addFromArray(json.Translations);
            if (fromTopLevel) {
                return fromTopLevel;
            }

            const translationNode = json.Translation;
            if (translationNode) {
                const fromTranslation =
                    addFromObject(translationNode.Translations) ||
                    addFromArray(translationNode.Translations) ||
                    addFromArray(translationNode) ||
                    addFromObject(translationNode);
                if (fromTranslation) {
                    return fromTranslation;
                }
            }

            return "";
        }

        async function startRecognition() {
            await ensureMicrophonePermission();

            const token = await getSpeechToken();
            const speechConfig = speechSdk.SpeechTranslationConfig.fromAuthorizationToken(token.token, token.region);
            speechConfig.speechRecognitionLanguage = sourceLanguageEl.value;
            speechConfig.outputFormat = speechSdk.OutputFormat.Detailed;
            speechConfig.addTargetLanguage(targetLanguageEl.value);

            const resolvedMicDeviceId = resolveDefaultAliasMicDeviceId();
            if (!getSelectedMicDeviceId() && resolvedMicDeviceId) {
                const mappedOption = Array.from(micInputEl.options)
                    .find(option => normalizeMicDeviceId(option.value) === resolvedMicDeviceId);
                if (mappedOption) {
                    micInputEl.value = resolvedMicDeviceId;
                    selectedMicDeviceId = resolvedMicDeviceId;
                    addSystemLine(`Default mic mapped to "${mappedOption.textContent.trim()}".`);
                }
            }
            let audioConfig = null;
            try {
                audioConfig = resolvedMicDeviceId
                    ? speechSdk.AudioConfig.fromMicrophoneInput(resolvedMicDeviceId)
                    : speechSdk.AudioConfig.fromDefaultMicrophoneInput();
            } catch {
                audioConfig = speechSdk.AudioConfig.fromDefaultMicrophoneInput();
            }

            recognizer = new speechSdk.TranslationRecognizer(speechConfig, audioConfig);

            recognizer.sessionStarted = () => {
                addSystemLine(`Session started (${sourceLanguageEl.value}).`);
            };

            recognizer.speechStartDetected = () => {
                addSystemLine("Speech start detected.");
            };

            recognizer.speechEndDetected = () => {
                addSystemLine("Speech end detected.");
            };

            recognizer.recognizing = (_, event) => {
                partialEl.textContent = event?.result?.text || "";
                translatedPartialEl.textContent = extractTranslatedText(event?.result, targetLanguageEl.value);
            };

            recognizer.recognized = (_, event) => {
                const reason = event?.result?.reason;
                const reasonName = speechSdk?.ResultReason?.[reason] || reason || "unknown";
                const text = extractRecognizedText(event?.result);
                const translatedText = extractTranslatedText(event?.result, targetLanguageEl.value);
                addSystemLine(
                    `Recognized event: ${reasonName}, sourceChars=${text.length}, targetChars=${translatedText.length}.`);

                const isFinalResult =
                    reason === speechSdk.ResultReason.RecognizedSpeech ||
                    reason === speechSdk.ResultReason.TranslatedSpeech;
                if (isFinalResult && (text || translatedText)) {
                    addFinalLine(`[src] ${text || "(empty)"}`);
                    addFinalLine(`[${targetLanguageEl.value}] ${translatedText || "(empty)"}`);
                    partialEl.textContent = "";
                    translatedPartialEl.textContent = "";
                    return;
                }

                if (isFinalResult && !text && !translatedText) {
                    const json = getSpeechServiceResultJson(event?.result);
                    if (json && typeof json === "object") {
                        addSystemLine(`Raw result fields: ${Object.keys(json).join(",")}`);
                        const preview = JSON.stringify(json).slice(0, 280);
                        addSystemLine(`Raw result preview: ${preview}`);
                    }
                }

                if (reason === speechSdk.ResultReason.NoMatch) {
                    let noMatchReason = "";
                    try {
                        const details = speechSdk.NoMatchDetails.fromResult(event.result);
                        noMatchReason = details?.reason ? ` (${details.reason})` : "";
                    } catch {
                        noMatchReason = "";
                    }
                    addSystemLine(`No recognizable speech detected${noMatchReason}.`);
                }
            };

            recognizer.canceled = (_, event) => {
                addSystemLine(`Canceled: ${event?.reason || "unknown"} ${event?.errorDetails || ""}`.trim());
            };

            recognizer.sessionStopped = () => {
                addSystemLine("Session stopped.");
            };

            await new Promise((resolve, reject) =>
                recognizer.startContinuousRecognitionAsync(resolve, reject));

            running = true;
            addSystemLine(`Listening with mic "${getSelectedMicLabel()}".`);
            setStatus("running");
        }

        async function stopRecognition() {
            if (!recognizer) {
                running = false;
                setStatus("idle");
                return;
            }

            const currentRecognizer = recognizer;
            recognizer = null;
            try {
                await new Promise((resolve, reject) =>
                    currentRecognizer.stopContinuousRecognitionAsync(resolve, reject));
            } catch {
                // Ignore stop errors.
            }

            currentRecognizer.close();
            running = false;
            partialEl.textContent = "";
            translatedPartialEl.textContent = "";
            setStatus("idle");
        }

        init().catch(err => {
            addSystemLine(`Init failed: ${err.message || err}`);
            setStatus("idle");
        });
