k3d cluster create --config k3d.yaml --registry-config k3d-registries.yaml

helm repo add istio https://istio-release.storage.googleapis.com/charts
helm repo update

# istio-base
helm install istio-base istio/base -n istio-system --create-namespace

# kubernetes gateway crd
if (-not (kubectl get crd gateways.gateway.networking.k8s.io -ErrorAction SilentlyContinue)) {
    kubectl apply -f https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.2.0/standard-install.yaml
}

# istiod
helm install istiod istio/istiod -n istio-system

# istio gateway
helm install istio-ingress istio/gateway -n istio-ingress --create-namespace

kubectl label namespace default istio-injection=enabled --overwrite

docker build -t localhost:5001/basic-service:latest -f .\BasicService\Dockerfile  .\BasicService\
docker push localhost:5001/basic-service:latest

$deployPath = "./deploy"
Get-ChildItem -Path $deployPath -Filter *.yaml | ForEach-Object {
    kubectl apply -f $_.FullName
}

docker build -t localhost:5001/isol8:latest -f ../src/Isol8/Dockerfile  ../src/
docker push localhost:5001/isol8:latest

helm install isol8 -n isol8 --create-namespace ..\deploy\