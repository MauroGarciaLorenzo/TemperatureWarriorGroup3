// @ts-nocheck

let chart;
const target_curve_id = 'target-curve';

const get_curve_dataset_index = () => {
    if (!chart) return -1;
    return chart.data.datasets.findIndex(ds => ds.id === target_curve_id);
};

const init_graph = () => {
    const ctx = document.getElementById('chart')?.getContext('2d');

    chart = new Chart(ctx, {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                {
                    label: 'Temperatura (°C)',
                    data: [],
                    borderWidth: 1,
                    pointBorderColor: [],
                    pointBackgroundColor: [],
                    segment: {
                        borderColor: seg => {
                            if (round_is_test) return '#5DADE2';
                            const p0 = is_on_range(seg.p0.parsed.y, seg.p0.parsed.x);
                            const p1 = is_on_range(seg.p1.parsed.y, seg.p1.parsed.x);
                            return p0 && p1 ? '#00FF88' : '#FF0000';
                        }
                    },
                },
            ],
        },
        options: {
            scales: {
                y: {
                    title: {
                        display: true,
                        text: 'Temperatura (°C)',
                    },
                    min: 0,
                    max: 40,
                },
                x: {
                    type: 'linear',
                    title: {
                        display: true,
                        text: 'Tiempo (s)',
                    },
                    min: 0,
                },
            },
            plugins: {
                legend: { display: false },
            }
        },
    });
};

const set_round_chart = ranges => {
    clear_graph();
    let totalTime = 0;
    ranges.forEach(range => {
        chart.data.datasets.push({
            data: [{x: totalTime, y: range.tempMin}, {x: totalTime+range.roundTime, y: range.tempMin}],
            pointRadius: 0,
            pointHitRadius: 0,
        });
        chart.data.datasets.push({ 
            data: [{x: totalTime, y: range.tempMax}, {x: totalTime+range.roundTime, y: range.tempMax}],
            pointRadius: 0,
            pointHitRadius: 0,
            fill: '-1',
        });
        totalTime += range.roundTime;
    });
    chart.options.scales.x.max = totalTime;
    chart.update();
};

const set_test_chart = () => {
    chart.data.datasets[0].data.length = 0;
    chart.data.datasets[0].pointBorderColor.length = 0;
    chart.data.datasets[0].pointBackgroundColor.length = 0;

    const curveIndex = get_curve_dataset_index();
    if (curveIndex >= 0 && chart.data.datasets[curveIndex].data.length > 0) {
        const last = chart.data.datasets[curveIndex].data[chart.data.datasets[curveIndex].data.length - 1];
        const curveMax = typeof last === 'object' ? last.x : 0;
        chart.options.scales.x.max = Math.max(60, curveMax || 0);
    } else {
        chart.options.scales.x.max = 60;
    }

    chart.update();
};

const add_chart_point = (x, y) => {
    chart.data.datasets[0].data.push({x, y});

    if (!round_is_test) {
        const on_range = is_on_range(y, x);
        const color = on_range ? '#00FF88' : '#FF0000';
        chart.data.datasets[0].pointBorderColor.push(color);
        chart.data.datasets[0].pointBackgroundColor.push(color);

        // also update the seconds in range span
        if (on_range) {
            let seconds = parseFloat(seconds_in_range_span.innerText);
            seconds_in_range_span.innerText = (seconds + refresh_rate).toFixed(1);
        }
    }
    chart.update();


};

const clear_graph = () => {
    chart.data.datasets.length = 1; // remove all other datasets
    chart.data.datasets[0].data.length = 0; // remove temperature points
}

const set_target_curve = points => {
    const curveDataset = {
        id: target_curve_id,
        label: 'Curva objetivo',
        data: points,
        borderWidth: 2,
        borderColor: '#444444',
        pointRadius: 0,
        pointHitRadius: 0,
        fill: false,
        borderDash: [6, 4],
    };

    const index = get_curve_dataset_index();
    if (index === -1) {
        chart.data.datasets.push(curveDataset);
    } else {
        chart.data.datasets[index] = curveDataset;
    }
};
