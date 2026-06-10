#!/bin/zsh
cd "/Users/deanmartin/Library/Mobile Documents/com~apple~CloudDocs/FrostAura Global/Technologies/projects/fa.foresight/research/btc-5min-direction/lab"
# wait until no chaos_harness invocation is running (max 40 min)
for i in $(seq 1 240); do
  pgrep -f "chaos_harness_v1.py experiments" > /dev/null || break
  sleep 10
done
python3 chaos_harness_v1.py experiments/a3_w_cbvn12_k3.py > experiments/a3_w_cbvn12_k3.harness.log 2>&1
python3 chaos_harness_v1.py experiments/a3_w_vn12mild_k3.py > experiments/a3_w_vn12mild_k3.harness.log 2>&1
echo QUEUE_DONE
