#!/usr/bin/env bash
set -euo pipefail

BATCH_ID="${2:-kitchen-subset-10h-v3-20260902}"
BATCH_ROOT="/data/mayanwen/kitchen-rollouts/${BATCH_ID}"
IMAGE="ccr-21dndou3-vpc.cnc.bj.baidubce.com/wanghy/ue_pipeline:v1.0-torch2.7.1-cu12.8_wanghaoyu_20260424155044"
NODE="${1:?usage: launch_10h_subset_node.sh g154|g155 [batch-id]}"

case "${NODE}" in
  g154|g155) ;;
  *) echo "unsupported node: ${NODE}" >&2; exit 2 ;;
esac

test -x "${BATCH_ROOT}/player/KitchenGame.x86_64"
test -d "${BATCH_ROOT}/tasks"

for gpu in $(seq 0 7); do
  task_files=("${BATCH_ROOT}/tasks/${NODE}-gpu${gpu}-"*.json)
  if [[ ${#task_files[@]} -ne 1 || ! -f "${task_files[0]}" ]]; then
    echo "expected one task for ${NODE} gpu ${gpu}" >&2
    exit 3
  fi

  worker_root="${BATCH_ROOT}/${NODE}/gpu_${gpu}"
  container="kitchen-${BATCH_ID}-${NODE}-gpu${gpu}"
  mkdir -p "${worker_root}/logs"
  chown -R 1000:1000 "${worker_root}"
  docker rm -f "${container}" >/dev/null 2>&1 || true

  docker run -d \
    --name "${container}" \
    --gpus "device=${gpu}" \
    --user 1000:1000 \
    -e NVIDIA_DRIVER_CAPABILITIES=graphics,utility,compute \
    -e "LD_LIBRARY_PATH=.:/host_nvidia" \
    -e VK_ICD_FILENAMES=/tmp/nvidia_egl_icd.json \
    -e XDG_RUNTIME_DIR=/tmp/runtime-1000 \
    -e "WORKER_ROOT=${worker_root}" \
    -e "TASK_FILE=${task_files[0]}" \
    -v /usr/lib/x86_64-linux-gnu:/host_nvidia:ro \
    -v "${BATCH_ROOT}:${BATCH_ROOT}" \
    -w "${BATCH_ROOT}/player" \
    "${IMAGE}" \
    bash -lc '
      mkdir -p /tmp/runtime-1000
      chmod 700 /tmp/runtime-1000
      launch_marker="${WORKER_ROOT}/.launch_started"
      touch "${launch_marker}"
      printf "%s\n" "{\"file_format_version\":\"1.0.1\",\"ICD\":{\"library_path\":\"/host_nvidia/libEGL_nvidia.so.0\",\"api_version\":\"1.4.303\"}}" > /tmp/nvidia_egl_icd.json
      xvfb-run -a ./KitchenGame.x86_64 \
        -batchmode \
        -force-vulkan \
        -logFile "${WORKER_ROOT}/logs/player.log" \
        --task "${TASK_FILE}"
      code=$?
      # Some NVIDIA/Vulkan stacks return 139 during Unity shutdown even after
      # the recorder has atomically finalized the session. Treat a fresh
      # _SUCCESS marker as the authoritative completion signal.
      if [[ "${code}" -ne 0 ]] && find "${WORKER_ROOT}/recordings" \
          -type f -name _SUCCESS -newer "${launch_marker}" -print -quit \
          | grep -q .; then
        code=0
      fi
      printf "%s\n" "${code}" > "${WORKER_ROOT}/exit_code"
      exit "${code}"
    '
done

docker ps --filter "name=kitchen-${BATCH_ID}-${NODE}" \
  --format '{{.Names}} {{.Status}}'
