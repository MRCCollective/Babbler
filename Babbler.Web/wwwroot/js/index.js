const supportedTargetLanguages = ["en", "sv", "es", "fr", "de", "it", "ja"];

const previewTargetLanguage = "en";

const statusEl = document.getElementById("status");
const roomInfoEl = document.getElementById("roomInfo");
const usageEl = document.getElementById("usage");
const sourcePanelEl = document.getElementById("sourcePanel");
const debugPanelEl = document.getElementById("debugPanel");
const joinUrlEl = document.getElementById("joinUrlValue");
const joinPinEl = document.getElementById("joinPinValue");
const openJoinUrlBtn = document.getElementById("openJoinUrlBtn");
const copyJoinUrlBtn = document.getElementById("copyJoinUrlBtn");
const copyJoinPinBtn = document.getElementById("copyJoinPinBtn");
const sourceLanguageEl = document.getElementById("sourceLanguage");
const micInputEl = document.getElementById("micInput");
const startBtn = document.getElementById("startBtn");
const stopBtn = document.getElementById("stopBtn");
const clearBtn = document.getElementById("clearBtn");
const debugToggleEl = document.getElementById("debugToggle");
const sourceCaptureToggleEl = document.getElementById("sourceCaptureToggle");
const clearSourceBtn = document.getElementById("clearSourceBtn");
const downloadSourceBtn = document.getElementById("downloadSourceBtn");
const sourceLogEl = document.getElementById("sourceLog");
const transcriptEl = document.getElementById("transcript");

const speechSdk = window.SpeechSDK;
const signalRClient = window.signalR;

let recognizer = null;
let running = false;
let selectedMicDeviceId = "";
let currentRoomId = "";
let currentJoinUrl = "";
let currentJoinPin = "";
let statusPollHandle = null;
let freeLimitReached = false;
let freeLimitNoticeShown = false;
let connection = null;
let lastPublishErrorAt = 0;
let finalizedSourceLines = [];
let currentSourcePartial = "";
let debugEnabled = false;
let sourceCaptureEnabled = false;

if (!speechSdk) {
    statusEl.textContent = "Status: Speech SDK failed to load.";
    throw new Error("Speech SDK failed to load.");
}

if (!signalRClient) {
    statusEl.textContent = "Status: SignalR client failed to load.";
    throw new Error("SignalR client failed to load.");
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
    await stopRecognition({ reason: "manual" });
});

clearBtn.addEventListener("click", () => {
    if (!debugEnabled) {
        return;
    }

    transcriptEl.replaceChildren();
});

debugToggleEl.addEventListener("change", () => {
    setDebugEnabled(debugToggleEl.checked);
});

sourceCaptureToggleEl.addEventListener("change", () => {
    setSourceCaptureEnabled(sourceCaptureToggleEl.checked);
});

clearSourceBtn.addEventListener("click", () => {
    if (!sourceCaptureEnabled) {
        return;
    }

    finalizedSourceLines = [];
    currentSourcePartial = "";
    renderSourceLog();
});

downloadSourceBtn.addEventListener("click", () => {
    downloadSourceLog();
});

copyJoinUrlBtn.addEventListener("click", async () => {
    await copyAccessValue(currentJoinUrl, "Join URL");
});

openJoinUrlBtn.addEventListener("click", () => {
    openJoinUrlInNewTab();
});

copyJoinPinBtn.addEventListener("click", async () => {
    await copyAccessValue(currentJoinPin, "PIN");
});

micInputEl.addEventListener("change", () => {
    selectedMicDeviceId = getSelectedMicDeviceId();
    addSystemLine(`Mic selected: ${getSelectedMicLabel()}`);
});

window.addEventListener("beforeunload", () => {
    if (!running) {
        return;
    }

    stopRecognition({ reason: "tab-close", quiet: true }).catch(() => {
        // Ignore shutdown errors.
    });
});

async function init() {
    setDebugEnabled(debugToggleEl.checked);
    setSourceCaptureEnabled(sourceCaptureToggleEl.checked);
    setStatus("initializing");
    renderSourceLog();
    await ensureRoom();
    await refreshAccessInfo();
    await connectToRoom(currentRoomId);
    await refreshSessionStatus();
    startStatusPolling();

    await ensureMicrophonePermission();
    await loadMicrophones(true);

    addSystemLine("Ready.");
    setStatus("idle");
}

function addSystemLine(message) {
    if (!debugEnabled) {
        return;
    }

    const line = document.createElement("p");
    line.className = "line system";
    line.textContent = `[system] ${message}`;
    transcriptEl.prepend(line);
}

function addFinalLine(message) {
    if (!debugEnabled) {
        return;
    }

    const line = document.createElement("p");
    line.className = "line";
    line.textContent = message;
    transcriptEl.prepend(line);
}

function setDebugEnabled(enabled) {
    debugEnabled = Boolean(enabled);
    clearBtn.disabled = !debugEnabled;
    debugPanelEl.classList.toggle("collapsed", !debugEnabled);

    if (!debugEnabled) {
        transcriptEl.replaceChildren();
        return;
    }

    addSystemLine("Debug output activated.");
}

function renderSourceLog() {
    if (!sourceCaptureEnabled) {
        sourceLogEl.value = "";
        sourceLogEl.scrollTop = 0;
        return;
    }

    const finalLines = finalizedSourceLines.map(item => `[${item.stamp}] ${item.text}`);
    const typingLine = currentSourcePartial
        ? [`[typing] ${currentSourcePartial}`]
        : [];

    sourceLogEl.value = [...finalLines, ...typingLine].join("\n");
    sourceLogEl.scrollTop = sourceLogEl.scrollHeight;
}

function logSourcePartial(text) {
    if (!sourceCaptureEnabled) {
        return;
    }

    const normalized = (text || "").trim();
    if (!normalized) {
        currentSourcePartial = "";
        renderSourceLog();
        return;
    }

    if (normalized === currentSourcePartial) {
        return;
    }

    currentSourcePartial = normalized;
    renderSourceLog();
}

function logSourceFinal(text) {
    if (!sourceCaptureEnabled) {
        return;
    }

    const normalized = (text || "").trim();
    if (!normalized || finalizedSourceLines.at(-1)?.text === normalized) {
        currentSourcePartial = "";
        renderSourceLog();
        return;
    }

    finalizedSourceLines.push({
        stamp: new Date().toLocaleTimeString(),
        text: normalized
    });
    currentSourcePartial = "";
    renderSourceLog();
}

function downloadSourceLog() {
    if (!sourceCaptureEnabled) {
        addSystemLine("Live source capture is disabled.");
        return;
    }

    const content = finalizedSourceLines
        .map(item => `[${item.stamp}] ${item.text}`)
        .join("\n")
        .trim();
    if (!content) {
        addSystemLine("No source text to download.");
        return;
    }

    const roomPart = (currentRoomId || "room").replace(/[^A-Z0-9_-]/gi, "");
    const stamp = new Date().toISOString().replace(/[:.]/g, "-");
    const filename = `babbler-source-${roomPart}-${stamp}.txt`;
    const blob = new Blob([content], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
    addSystemLine("Source log downloaded.");
}

function setSourceCaptureEnabled(enabled) {
    sourceCaptureEnabled = Boolean(enabled);
    clearSourceBtn.disabled = !sourceCaptureEnabled;
    downloadSourceBtn.disabled = !sourceCaptureEnabled;
    sourcePanelEl.classList.toggle("collapsed", !sourceCaptureEnabled);

    if (!sourceCaptureEnabled) {
        finalizedSourceLines = [];
        currentSourcePartial = "";
    }

    renderSourceLog();
}

function setJoinAccessInfo(joinUrl, pin) {
    currentJoinUrl = (joinUrl || "").trim();
    currentJoinPin = (pin || "").trim();

    joinUrlEl.textContent = currentJoinUrl || "unavailable";
    joinPinEl.textContent = currentJoinPin || "unavailable";

    openJoinUrlBtn.disabled = !currentJoinUrl;
    copyJoinUrlBtn.disabled = !currentJoinUrl;
    copyJoinPinBtn.disabled = !currentJoinPin;
}

function openJoinUrlInNewTab() {
    if (!currentJoinUrl) {
        addSystemLine("Join URL unavailable.");
        return;
    }

    const opened = window.open(currentJoinUrl, "_blank", "noopener");
    if (!opened) {
        addSystemLine("Failed to open Join URL (popup blocked).");
    }
}

async function copyAccessValue(value, label) {
    if (!value) {
        addSystemLine(`${label} unavailable.`);
        return;
    }

    try {
        await copyToClipboard(value);
        addSystemLine(`${label} copied.`);
    } catch {
        addSystemLine(`Failed to copy ${label}.`);
    }
}

async function copyToClipboard(text) {
    if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(text);
        return;
    }

    const textArea = document.createElement("textarea");
    textArea.value = text;
    textArea.setAttribute("readonly", "");
    textArea.style.position = "fixed";
    textArea.style.left = "-9999px";
    document.body.appendChild(textArea);
    textArea.select();
    document.execCommand("copy");
    document.body.removeChild(textArea);
}

function setStatus(state) {
    if (state === "running") {
        statusEl.textContent = `Status: listening (${sourceLanguageEl.value})`;
        startBtn.disabled = true;
        stopBtn.disabled = false;
        sourceLanguageEl.disabled = true;
        micInputEl.disabled = true;
        return;
    }

    if (state === "initializing") {
        statusEl.textContent = "Status: initializing...";
        startBtn.disabled = true;
        stopBtn.disabled = true;
        sourceLanguageEl.disabled = true;
        micInputEl.disabled = true;
        return;
    }

    statusEl.textContent = freeLimitReached
        ? "Status: free minutes exhausted"
        : "Status: idle";

    startBtn.disabled = freeLimitReached;
    stopBtn.disabled = true;
    sourceLanguageEl.disabled = false;
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
            if ((key || "").trim().toLowerCase() === target) {
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

function extractTranslationsMap(result) {
    const translations = {};
    for (const language of supportedTargetLanguages) {
        const text = extractTranslatedText(result, language);
        if (text) {
            translations[language] = text;
        }
    }

    return translations;
}

async function ensureRoom() {
    const requestedRoomId = getRoomIdFromQuery();
    if (requestedRoomId) {
        currentRoomId = requestedRoomId;
        roomInfoEl.textContent = `Room: ${currentRoomId}`;

        const statusResponse = await fetchJson(`${roomApiBase(currentRoomId)}/session/status`);
        if (statusResponse.ok) {
            setRoomIdInQuery(currentRoomId);
            updateFromStatus(statusResponse.data);
            return;
        }
    }

    await createAndSelectRoom();
}

async function createAndSelectRoom() {
    const createResponse = await fetchJson("/api/rooms", { method: "POST" });
    if (!createResponse.ok || !createResponse.data?.roomId) {
        throw new Error(createResponse.errorMessage || "Failed to create room.");
    }

    currentRoomId = createResponse.data.roomId.toString().trim().toUpperCase();
    setRoomIdInQuery(currentRoomId);
    roomInfoEl.textContent = `Room: ${currentRoomId}`;
    addSystemLine(`Room ready: ${currentRoomId}`);
}

async function refreshAccessInfo() {
    if (!currentRoomId) {
        return;
    }

    const response = await fetchJson(`${roomApiBase()}/access-info`);
    if (!response.ok || !response.data) {
        setJoinAccessInfo("", "");
        return;
    }

    const joinUrl = response.data.joinUrl || `${window.location.origin}/join.html?roomId=${encodeURIComponent(currentRoomId)}`;
    const pin = response.data.pin || "------";
    setJoinAccessInfo(joinUrl, pin);
}

function updateFromStatus(state) {
    if (!state) {
        return;
    }

    const used = formatMinutes(state.freeMinutesUsed);
    const limit = formatMinutes(state.freeMinutesLimit);
    const left = formatMinutes(state.freeMinutesRemaining);
    usageEl.textContent = `Free usage: ${used} / ${limit} min used, ${left} min left`;

    const wasFreeLimitReached = freeLimitReached;
    freeLimitReached = Boolean(state.freeLimitReached);

    if (freeLimitReached && !wasFreeLimitReached && !freeLimitNoticeShown) {
        freeLimitNoticeShown = true;
        addSystemLine("Free translation minutes are exhausted.");
    }

    if (!freeLimitReached) {
        freeLimitNoticeShown = false;
    }

    if (currentRoomId) {
        const runningText = state.isRunning ? "running" : "stopped";
        roomInfoEl.textContent = `Room: ${currentRoomId} (${runningText})`;
    }

    if (!running) {
        setStatus("idle");
    }
}

async function refreshSessionStatus() {
    if (!currentRoomId) {
        return null;
    }

    const response = await fetchJson(`${roomApiBase()}/session/status`);
    if (!response.ok) {
        if (response.status === 404) {
            addSystemLine("Room no longer exists. Creating a new room.");
            await createAndSelectRoom();
            await refreshAccessInfo();
            await connectToRoom(currentRoomId);
            return await refreshSessionStatus();
        }

        throw new Error(response.errorMessage || "Failed to load status.");
    }

    updateFromStatus(response.data);

    if (running && !response.data?.isRunning) {
        await stopRecognition({ skipServerStop: true, quiet: true });
        addSystemLine("Session stopped by server.");
    }

    return response.data;
}

function startStatusPolling() {
    if (statusPollHandle !== null) {
        clearInterval(statusPollHandle);
    }

    statusPollHandle = setInterval(async () => {
        try {
            await refreshSessionStatus();
        } catch {
            // Ignore transient polling errors.
        }
    }, 5000);
}

function formatMinutes(value) {
    const numeric = Number(value);
    if (!Number.isFinite(numeric)) {
        return "0.00";
    }

    return numeric.toFixed(2);
}

async function connectToRoom(roomId) {
    if (!roomId) {
        return;
    }

    if (connection) {
        try {
            await connection.stop();
        } catch {
            // Ignore stop errors.
        }
    }

    const nextConnection = new signalRClient.HubConnectionBuilder()
        .withUrl(`/hubs/translation?roomId=${encodeURIComponent(roomId)}`, {
            transport: signalRClient.HttpTransportType.LongPolling
        })
        .withAutomaticReconnect()
        .build();

    nextConnection.onreconnecting(() => {
        addSystemLine("Live feed reconnecting...");
    });

    nextConnection.onreconnected(() => {
        addSystemLine("Live feed reconnected.");
    });

    nextConnection.onclose(() => {
        addSystemLine("Live feed disconnected.");
    });

    await nextConnection.start();
    connection = nextConnection;
}

function getPreviewTranslation(translations, fallbackResult) {
    const selectedTarget = previewTargetLanguage;
    if (translations[selectedTarget]) {
        return translations[selectedTarget];
    }

    return extractTranslatedText(fallbackResult, selectedTarget);
}

function publishRecognitionResult(sourceText, isFinal, translations) {
    if (!connection ||
        connection.state !== signalRClient.HubConnectionState.Connected) {
        return;
    }

    const safeSourceText = (sourceText || "").trim();
    const safeTranslations = {};
    for (const [key, value] of Object.entries(translations || {})) {
        const normalizedKey = (key || "").trim().toLowerCase();
        const normalizedValue = (value || "").trim();
        if (!normalizedKey || !normalizedValue) {
            continue;
        }

        safeTranslations[normalizedKey] = normalizedValue;
    }

    if (!safeSourceText && Object.keys(safeTranslations).length === 0) {
        return;
    }

    connection.invoke("PublishClientTranslation", {
        sourceText: safeSourceText || null,
        sourceLanguage: sourceLanguageEl.value,
        isFinal,
        translations: safeTranslations
    }).catch(err => {
        const now = Date.now();
        if (now - lastPublishErrorAt > 5000) {
            lastPublishErrorAt = now;
            addSystemLine(`Publish failed: ${err?.message || err}`);
        }
    });
}

async function startRecognition() {
    if (!currentRoomId) {
        await ensureRoom();
        await refreshAccessInfo();
        await connectToRoom(currentRoomId);
    }

    if (freeLimitReached) {
        throw new Error("Free translation minutes are exhausted.");
    }

    await ensureMicrophonePermission();

    const startResponse = await fetchJson(
        `${roomApiBase()}/session/start`,
        {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                sourceLanguage: sourceLanguageEl.value,
                targetLanguage: "en"
            })
        });

    if (!startResponse.ok) {
        throw new Error(startResponse.errorMessage || "Failed to start room session.");
    }

    updateFromStatus(startResponse.data);

    let recognizerStarted = false;
    try {
        const token = await getSpeechToken();
        const speechConfig = speechSdk.SpeechTranslationConfig.fromAuthorizationToken(token.token, token.region);
        speechConfig.speechRecognitionLanguage = sourceLanguageEl.value;
        speechConfig.outputFormat = speechSdk.OutputFormat.Detailed;
        for (const language of supportedTargetLanguages) {
            speechConfig.addTargetLanguage(language);
        }

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
            const sourceText = (event?.result?.text || "").trim();
            const translations = extractTranslationsMap(event?.result);

            logSourcePartial(sourceText);
            publishRecognitionResult(sourceText, false, translations);
        };

        recognizer.recognized = (_, event) => {
            const reason = event?.result?.reason;
            const reasonName = speechSdk?.ResultReason?.[reason] || reason || "unknown";
            const sourceText = extractRecognizedText(event?.result);
            const translations = extractTranslationsMap(event?.result);
            const previewText = getPreviewTranslation(translations, event?.result);

            addSystemLine(
                `Recognized event: ${reasonName}, sourceChars=${sourceText.length}, targetChars=${(previewText || "").length}.`);

            const isFinalResult =
                reason === speechSdk.ResultReason.RecognizedSpeech ||
                reason === speechSdk.ResultReason.TranslatedSpeech;

            if (isFinalResult) {
                publishRecognitionResult(sourceText, true, translations);
                logSourceFinal(sourceText);

                if (sourceText || previewText) {
                    addFinalLine(`[src] ${sourceText || "(empty)"}`);
                    addFinalLine(`[${previewTargetLanguage}] ${previewText || "(empty)"}`);
                }
                return;
            }

            if (reason === speechSdk.ResultReason.NoMatch) {
                addSystemLine("No recognizable speech detected.");
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

        recognizerStarted = true;
        running = true;
        addSystemLine(`Listening with mic "${getSelectedMicLabel()}".`);
        setStatus("running");
    } catch (err) {
        if (recognizer) {
            try {
                recognizer.close();
            } catch {
                // Ignore close errors.
            }
            recognizer = null;
        }

        if (!recognizerStarted) {
            await stopRoomOnServer("recognizer-start-failed", true);
        }

        throw err;
    }
}

async function stopRecognition(options = {}) {
    const { skipServerStop = false, quiet = false, reason = "manual" } = options;

    if (recognizer) {
        const currentRecognizer = recognizer;
        recognizer = null;
        try {
            await new Promise((resolve, reject) =>
                currentRecognizer.stopContinuousRecognitionAsync(resolve, reject));
        } catch {
            // Ignore stop errors.
        }

        currentRecognizer.close();
    }

    running = false;
    currentSourcePartial = "";
    renderSourceLog();

    if (!skipServerStop && currentRoomId) {
        await stopRoomOnServer(reason);
    }

    if (!quiet) {
        addSystemLine("Session stopped.");
    }

    setStatus("idle");
}

async function stopRoomOnServer(reason, suppressErrors = false) {
    if (!currentRoomId) {
        return;
    }

    const response = await fetchJson(
        `${roomApiBase()}/session/stop?reason=${encodeURIComponent(reason || "manual")}`,
        { method: "POST" });

    if (!response.ok) {
        if (!suppressErrors) {
            throw new Error(response.errorMessage || "Failed to stop room session.");
        }
        return;
    }

    updateFromStatus(response.data);
}

function getRoomIdFromQuery() {
    const params = new URLSearchParams(window.location.search);
    return (params.get("roomId") || "").trim().toUpperCase();
}

function setRoomIdInQuery(roomId) {
    const url = new URL(window.location.href);
    url.searchParams.set("roomId", roomId);
    const query = url.searchParams.toString();
    const finalUrl = query ? `${url.pathname}?${query}` : url.pathname;
    window.history.replaceState({}, "", finalUrl);
}

function roomApiBase(roomId = currentRoomId) {
    return `/api/rooms/${encodeURIComponent(roomId)}`;
}

async function fetchJson(url, options) {
    const response = await fetch(url, options || {});
    const raw = await response.text();

    let data = null;
    if (raw) {
        try {
            data = JSON.parse(raw);
        } catch {
            data = null;
        }
    }

    let errorMessage = `Request failed (${response.status}).`;
    if (data && data.error) {
        errorMessage = data.error;
    } else if (raw) {
        errorMessage = raw;
    }

    return {
        ok: response.ok,
        status: response.status,
        data,
        errorMessage
    };
}

init().catch(err => {
    addSystemLine(`Init failed: ${err.message || err}`);
    setStatus("idle");
});

