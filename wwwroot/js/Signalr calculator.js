// Steam Value Calculator - SignalR Version
// Versão mais robusta usando SignalR ao invés de SSE

// Importar biblioteca SignalR
// Adicione no head do HTML: <script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@latest/dist/browser/signalr.min.js"></script>

document.addEventListener('DOMContentLoaded', function () {
    const form = document.getElementById('calculatorForm');
    const btnCalculate = document.getElementById('btnCalculate');
    const progressContainer = document.getElementById('progressContainer');
    const progressBar = document.getElementById('progressBar');
    const progressPercentage = document.getElementById('progressPercentage');
    const statusMessage = document.getElementById('statusMessage');
    const resultsContainer = document.getElementById('resultsContainer');
    const resultsContent = document.getElementById('resultsContent');
    const errorContainer = document.getElementById('errorContainer');

    const btnProfileSummary = document.getElementById('btnProfileSummary');
    const btnGetFriends = document.getElementById('btnGetFriends');
    const btnGetSnapshots = document.getElementById('btnGetSnapshots');
    const btnExportCsv = document.getElementById('btnExportCsv');
    const sortSelect = document.getElementById('sortSelect');
    const filterMaxValue = document.getElementById('filterMaxValue');
    const btnApplyFilter = document.getElementById('btnApplyFilter');

    let currentProgress = 0;
    let totalValue = 0;
    let connection = null;

    // local state
    let latestGames = [];
    let latestInventories = {}; // gameName => items[]
    let latestProfile = null;
    let latestFriends = [];
    let latestSnapshots = [];

    // Chart instance
    let historyChart = null;

    // Helper mapping for game name -> appId (for market overview)
    const gameAppIdMap = { 'CS2': 730, 'Dota 2': 570, 'TF2': 440 };

    // Configurar conexão SignalR
    function setupSignalR() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl("/calculationHub")
            .withAutomaticReconnect()
            .configureLogging(signalR.LogLevel.Information)
            .build();

        // Event Handlers
        connection.on("UpdateProgress", (progress, message) => {
            updateProgress(progress, message);
        });

        connection.on("ReceiveGamesData", (games, total) => {
            // games is array of {name,value,imageUrl,appId,playtimeMinutes}
            latestGames = games || [];
            renderGames();
            totalValue += total || 0;
        });

        connection.on("ReceiveInventoryData", (game, items, total) => {
            latestInventories[game] = items || [];
            renderInventorySection(game, items || []);
            totalValue += total || 0;
        });

        connection.on("ReceiveTotalValue", (total) => {
            addTotalSection(total);
        });

        connection.on("ReceiveProfileSummary", (summary) => {
            // summary is likely the JSON structure returned by GetPlayerSummaries
            latestProfile = summary;
            renderProfileSummary(summary);
        });

        connection.on("ReceiveFriends", (friends) => {
            latestFriends = friends || [];
            renderFriendsList(latestFriends);
        });

        connection.on("ReceiveSnapshots", (snaps) => {
            latestSnapshots = snaps || [];
            renderHistoryChart(latestSnapshots);
        });

        connection.on("ReceiveAchievements", (appId, percent) => {
            showAchievements(appId, percent);
        });

        connection.on("ReceiveMarketOverview", (appId, marketHashName, price) => {
            showMarketOverview(appId, marketHashName, price);
        });

        connection.on("ReceiveFriendTotal", (steamId, total) => {
            // append or update friend total UI
            const el = document.querySelector(`[data-friend='${steamId}']`);
            if (el) {
                el.querySelector('.friend-total').textContent = `R$ ${Number(total).toFixed(2)}`;
            }
        });

        connection.on("ReceiveError", (message) => {
            showError(message);
            resetForm();
        });

        // Reconexão automática
        connection.onreconnecting(() => {
            console.log("Reconectando...");
            updateProgress(currentProgress, "Reconectando...");
        });

        connection.onreconnected(() => {
            console.log("Reconectado!");
        });

        connection.onclose(() => {
            console.log("Conexão fechada");
        });

        // Iniciar conexão
        return connection.start()
            .then(() => {
                console.log("SignalR conectado!");
            })
            .catch(err => {
                console.error("Erro ao conectar SignalR:", err);
                showError("Erro ao conectar com o servidor. Recarregue a página.");
            });
    }

    // Inicializar SignalR ao carregar a página
    setupSignalR();

    // Event Listener do formulário
    form.addEventListener('submit', async (e) => {
        e.preventDefault();

        // Verificar se está conectado
        if (connection.state !== signalR.HubConnectionState.Connected) {
            showError("Aguarde, conectando ao servidor...");
            await setupSignalR();
        }

        // Reset
        progressContainer.classList.add('active');
        resultsContainer.classList.remove('active');
        resultsContent.innerHTML = '';
        errorContainer.innerHTML = '';
        totalValue = 0;
        currentProgress = 0;

        latestGames = [];
        latestInventories = {};

        btnCalculate.disabled = true;
        btnCalculate.innerHTML = '<span>Calculando...</span>';

        const profileUrl = document.getElementById('profileUrl').value;
        const calculateGames = document.getElementById('calculateGames').checked;
        const calculateInventory = document.getElementById('calculateInventory').checked;

        try {
            // Invocar método do Hub
            await connection.invoke("StartCalculation", profileUrl, calculateGames, calculateInventory);

            // Aguardar conclusão (será tratado pelos eventos)
            setTimeout(() => {
                if (currentProgress >= 100) {
                    resetForm();
                }
            }, 2000);

        } catch (error) {
            console.error('Error:', error);
            showError('Erro ao calcular valores. Verifique o link do perfil e tente novamente.');
            resetForm();
        }
    });

    // Profile summary button
    btnProfileSummary.addEventListener('click', async () => {
        const profileUrl = document.getElementById('profileUrl').value;
        if (!profileUrl) return showError('Informe o link do perfil');
        await connection.invoke('GetProfileSummary', profileUrl);
    });

    // Friends button
    btnGetFriends.addEventListener('click', async () => {
        const profileUrl = document.getElementById('profileUrl').value;
        if (!profileUrl) return showError('Informe o link do perfil');
        await connection.invoke('GetFriends', profileUrl);
    });

    // Snapshots button
    btnGetSnapshots.addEventListener('click', async () => {
        const profileUrl = document.getElementById('profileUrl').value;
        if (!profileUrl) return showError('Informe o link do perfil');
        await connection.invoke('GetSnapshots', profileUrl);
    });

    btnExportCsv.addEventListener('click', () => {
        if (!latestGames || latestGames.length === 0) return showError('Nenhum jogo para exportar');
        const rows = [['AppId', 'Name', 'PlaytimeMinutes', 'Price']];
        latestGames.forEach(g => rows.push([g.appId, g.name, g.playtimeMinutes || 0, Number(g.value).toFixed(2)]));
        const csv = rows.map(r => r.map(c => '"' + String(c).replace(/"/g, '""') + '"').join(',')).join('\n');
        const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'games_export.csv';
        a.click();
        URL.revokeObjectURL(url);
    });

    btnApplyFilter.addEventListener('click', () => renderGames());
    sortSelect.addEventListener('change', () => renderGames());

    function updateProgress(progress, message) {
        currentProgress = progress;
        progressBar.style.width = progress + '%';
        progressPercentage.textContent = Math.round(progress) + '%';
        statusMessage.innerHTML = message + '<span class="loading-dots"><span>.</span><span>.</span><span>.</span></span>';

        // Auto-esconder após conclusão
        if (progress >= 100) {
            setTimeout(() => {
                progressContainer.classList.remove('active');
            }, 1500);
        }
    }

    function renderGames() {
        resultsContainer.classList.add('active');
        // remove existing games section if present
        const existing = document.querySelector('.results-section[data-type="games"]');
        if (existing) existing.remove();

        const section = document.createElement('div');
        section.className = 'results-section';
        section.setAttribute('data-type', 'games');
        section.innerHTML = `
            <div class="results-header">
                <span class="results-icon">🎮</span>
                <h2 class="results-title">Jogos</h2>
            </div>
            <div class="item-list" id="gamesList"></div>
        `;
        resultsContent.prepend(section);
        const gamesList = section.querySelector('#gamesList');

        let items = Array.from(latestGames);
        const maxVal = parseFloat(filterMaxValue.value || '0');
        if (!isNaN(maxVal) && maxVal > 0) {
            items = items.filter(i => Number(i.value) <= maxVal);
        }

        const sort = sortSelect.value;
        items.sort((a, b) => {
            if (sort === 'value_desc') return Number(b.value) - Number(a.value);
            if (sort === 'value_asc') return Number(a.value) - Number(b.value);
            if (sort === 'playtime_desc') return (b.playtimeMinutes || 0) - (a.playtimeMinutes || 0);
            if (sort === 'name_asc') return String(a.name).localeCompare(String(b.name));
            return 0;
        });

        items.forEach((item, index) => {
            const itemEl = document.createElement('div');
            itemEl.className = 'item';
            itemEl.innerHTML = `
                <div style="display:flex;align-items:center;gap:12px">
                    <img src="${escapeHtml(item.imageUrl || ('https://cdn.akamai.steamstatic.com/steam/apps/' + item.appId + '/header.jpg'))}" alt="${escapeHtml(item.name)}" style="width:64px;height:36px;object-fit:cover;border-radius:6px;border:1px solid rgba(255,255,255,0.04)"/>
                    <div style="flex:1">
                        <div class="item-name">${escapeHtml(item.name)}</div>
                        <div style="display:flex;gap:12px;align-items:center;">
                            <div class="item-value">R$ ${Number(item.value).toFixed(2)}</div>
                            <div style="color:var(--text-secondary);font-size:0.9rem">Playtime: ${Math.round((item.playtimeMinutes||0)/60)}h</div>
                        </div>
                    </div>
                    <div style="display:flex;flex-direction:column;gap:6px;align-items:flex-end">
                        <button class="btn-calculate btn-small" data-appid="${item.appId}" data-name="${escapeHtml(item.name)}">Ver Conquistas</button>
                        <button class="btn-calculate btn-small" data-appid="${item.appId}" data-name="${escapeHtml(item.name)}">Market</button>
                    </div>
                </div>
            `;

            // attach achievement handler
            const achBtn = itemEl.querySelectorAll('.btn-calculate')[0];
            achBtn.addEventListener('click', () => {
                const profileUrl = document.getElementById('profileUrl').value;
                if (!profileUrl) return showError('Informe o link do perfil');
                const appId = parseInt(achBtn.getAttribute('data-appid'));
                connection.invoke('GetAchievements', profileUrl, appId);
            });

            // market handler
            const marketBtn = itemEl.querySelectorAll('.btn-calculate')[1];
            marketBtn.addEventListener('click', () => {
                const appId = parseInt(marketBtn.getAttribute('data-appid'));
                const marketHashName = marketBtn.getAttribute('data-name');
                connection.invoke('GetMarketOverview', appId, marketHashName);
            });

            gamesList.appendChild(itemEl);
        });
    }

    function renderInventorySection(game, items) {
        // remove existing inventory section for this game
        const existing = document.querySelector(`.results-section[data-game='${game}']`);
        if (existing) existing.remove();

        const section = document.createElement('div');
        section.className = 'results-section';
        section.setAttribute('data-game', game);
        section.innerHTML = `
            <div class="results-header">
                <span class="results-icon">${game === 'CS2' ? '🔫' : game === 'Dota 2' ? '⚔️' : '📦'}</span>
                <h2 class="results-title">${escapeHtml(game)}</h2>
            </div>
            <div class="item-list" id="inventoryList${game.replace(/\s/g, '')}"></div>
        `;
        resultsContent.appendChild(section);
        const inventoryList = section.querySelector(`#inventoryList${game.replace(/\s/g, '')}`);

        items.forEach((item, index) => {
            const itemEl = document.createElement('div');
            itemEl.className = 'item';

            const imgHtml = item.imageUrl ? `<img src="${escapeHtml(item.imageUrl)}" alt="${escapeHtml(item.name)}" style="width:48px;height:48px;object-fit:cover;border-radius:6px;border:1px solid rgba(255,255,255,0.04)"/>` : '';

            itemEl.innerHTML = `
                <div style="display:flex;align-items:center;gap:12px">
                    ${imgHtml}
                    <div style="flex:1">
                        <div class="item-name">${escapeHtml(item.name)}</div>
                        <div class="item-value">R$ ${Number(item.value).toFixed(2)}</div>
                    </div>
                    <div style="display:flex;flex-direction:column;gap:6px;align-items:flex-end">
                        <button class="btn-calculate btn-small" data-game="${escapeHtml(game)}" data-name="${escapeHtml(item.name)}">Market</button>
                    </div>
                </div>
            `;

            const marketBtn = itemEl.querySelector('.btn-calculate');
            marketBtn.addEventListener('click', () => {
                const g = marketBtn.getAttribute('data-game');
                const name = marketBtn.getAttribute('data-name');
                const appId = gameAppIdMap[g] || 0;
                connection.invoke('GetMarketOverview', appId, name);
            });

            inventoryList.appendChild(itemEl);
        });
    }

    function addTotalSection(total) {
        const totalSection = document.createElement('div');
        totalSection.className = 'total-value';
        totalSection.innerHTML = `
            <div class="total-label">💰 Valor Total da Conta</div>
            <div class="total-amount">R$ ${Number(total).toFixed(2)}</div>
        `;
        resultsContent.appendChild(totalSection);
    }

    function renderProfileSummary(summary) {
        const panel = document.getElementById('profilePanel');
        panel.innerHTML = '';
        try {
            const player = (summary && summary.response && summary.response.players && summary.response.players[0]) || null;
            if (!player) return panel.innerHTML = '<div class="results-section">Perfil não encontrado</div>';
            const avatar = player.avatarfull || player.avatar || '';
            const name = player.personaname || '';
            const country = player.loccountrycode || '';
            const lastlogoff = player.lastlogoff ? new Date(player.lastlogoff * 1000).toLocaleString() : 'N/A';

            panel.innerHTML = `
                <div class="results-section">
                    <div style="display:flex;gap:12px;align-items:center">
                        <img src="${avatar}" style="width:64px;height:64px;border-radius:8px;object-fit:cover" />
                        <div>
                            <div style="font-weight:700">${escapeHtml(name)}</div>
                            <div style="color:var(--text-secondary)">${escapeHtml(country)} • Último logon: ${escapeHtml(lastlogoff)}</div>
                        </div>
                    </div>
                </div>
            `;
        } catch (e) {
            panel.innerHTML = '<div class="results-section">Erro ao renderizar perfil</div>';
        }
    }

    function renderFriendsList(friends) {
        const panel = document.getElementById('friendsPanel');
        panel.innerHTML = '';
        if (!friends || friends.length === 0) return panel.innerHTML = '<div class="results-section">Nenhum amigo encontrado</div>';
        const list = document.createElement('div');
        list.className = 'results-section';
        list.innerHTML = `<h3 class="results-title">Amigos (${friends.length})</h3>`;
        const container = document.createElement('div');
        container.style.display = 'flex';
        container.style.flexDirection = 'column';
        container.style.gap = '8px';
        friends.forEach(sid => {
            const row = document.createElement('div');
            row.style.display = 'flex';
            row.style.justifyContent = 'space-between';
            row.style.alignItems = 'center';
            row.setAttribute('data-friend', sid);
            row.innerHTML = `<div style="font-size:0.9rem">${escapeHtml(sid)}</div><div class="friend-total">-</div>`;
            const btn = document.createElement('button');
            btn.className = 'btn-calculate';
            btn.style.marginLeft = '8px';
            btn.textContent = 'Comparar';
            btn.addEventListener('click', () => {
                // compute total for this friend
                connection.invoke('ComputeTotalsForSteamId', sid, true, true);
            });
            row.appendChild(btn);
            container.appendChild(row);
        });
        list.appendChild(container);
        panel.appendChild(list);

        // add compare all
        const compareAll = document.createElement('div');
        compareAll.style.marginTop = '8px';
        compareAll.innerHTML = `<button id="compareAllBtn" class="btn-calculate">Comparar todos (rápido)</button>`;
        panel.appendChild(compareAll);
        document.getElementById('compareAllBtn').addEventListener('click', () => {
            friends.slice(0, 20).forEach(sid => connection.invoke('ComputeTotalsForSteamId', sid, true, false));
        });
    }

    function renderHistoryChart(snaps) {
        const ctx = document.getElementById('historyChart').getContext('2d');
        const labels = snaps.map(s => new Date(s[0]).toLocaleString());
        const data = snaps.map(s => s[1]);
        if (historyChart) {
            historyChart.data.labels = labels;
            historyChart.data.datasets[0].data = data;
            historyChart.update();
            return;
        }
        historyChart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    label: 'Valor da conta (R$)',
                    data: data,
                    borderColor: 'rgba(0,217,255,0.8)',
                    backgroundColor: 'rgba(0,217,255,0.1)',
                    fill: true,
                }]
            },
            options: {
                responsive: true,
                scales: {
                    x: { display: true },
                    y: { display: true }
                }
            }
        });
    }

    function showAchievements(appId, percent) {
        const modal = document.getElementById('achievementsModal');
        document.getElementById('achTitle').textContent = `Conquistas - App ${appId}`;
        document.getElementById('achContent').innerHTML = `<div>Percentual desbloqueado: ${Number(percent).toFixed(2)}%</div>`;
        modal.style.display = 'flex';
    }

    document.getElementById('closeAch').addEventListener('click', () => document.getElementById('achievementsModal').style.display = 'none');

    function showMarketOverview(appId, marketHashName, price) {
        const modal = document.getElementById('marketModal');
        document.getElementById('marketTitle').textContent = `Market - ${marketHashName}`;
        document.getElementById('marketContent').innerHTML = `<div>AppId: ${appId}</div><div>Preço: R$ ${Number(price).toFixed(2)}</div>`;
        modal.style.display = 'flex';
    }

    document.getElementById('closeMarket').addEventListener('click', () => document.getElementById('marketModal').style.display = 'none');

    function showError(message) {
        errorContainer.innerHTML = `
            <div class="error-message">
                <strong>❌ Erro:</strong> ${escapeHtml(message)}
            </div>
        `;
    }

    function resetForm() {
        btnCalculate.disabled = false;
        btnCalculate.innerHTML = '<span>Calcular Novamente</span>';
        progressContainer.classList.remove('active');
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
});