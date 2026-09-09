import { useQuery } from "@tanstack/react-query";
import { client } from "./query-client";

export function useModelConflicts(): boolean {
  const { data: models } = useQuery({
    queryKey: ["models"],
    queryFn: () => client.listModels(),
    staleTime: 30_000,
  });
  return models?.some((m: any) => m.status === "conflict") ?? false;
}
