alter function public.submit_score(bigint, smallint, integer) set search_path = '';
alter function public.get_top_scores(integer) set search_path = '';
alter function public.get_seed_top_scores(smallint, integer) set search_path = '';
alter function public.get_seed_rank(bigint, smallint) set search_path = '';
